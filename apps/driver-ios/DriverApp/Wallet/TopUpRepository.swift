import Foundation
import MageRideShared

/// SCR-DI-022 — the two top-up rails and the bulk-voucher ladder.
///
/// ### There is no third rail (AL-05)
///
/// Card and OnePay wallet are **one endpoint**, `POST /v1/wallet/topup/onepay`, because the
/// card-versus-wallet choice is made on OnePay's own hosted page; LankaQR is the other. Bank transfer
/// is not a top-up method and its route was deleted rather than deprecated — `wallet.yaml` carries no
/// `POST /v1/wallet/topup/bank-transfer`, `WalletApi` has no such call, and `MoneyDomainHygieneTest`
/// fails `:shared`'s build if the words reappear in that package. Nothing here may add one.
///
/// ### Buying a voucher is NOT topping up the discounted price
///
/// A voucher rides subscription-svc's `POST /v1/vouchers/purchase`, which initiates the gateway payment
/// **and** posts the credit on confirmation. Initiating a `topupWithOnepay(90000)` and then calling a
/// purchase would credit Rs 900 on the webhook *and* Rs 1,000 on the purchase — Rs 1,900 for a Rs 1,000
/// voucher. wallet-svc's own `purchaseVoucherFromWallet` takes an already-settled `gatewayRef` and
/// initiates nothing, so it is the wrong half of the pair for a screen: it is the reconciliation entry
/// point, not the buy button. See the C073 handoff.
protocol TopUpRepository: AnyObject {

    /// `GET /v1/wallet/voucher/discount-tiers` — the denominations on sale and their discounts.
    func voucherTiers() async throws -> VoucherCatalogue

    /// Starts a plain top-up of `amountMinor` on `method`.
    func topUp(method: TopupMethod, amountMinor: Int64) async throws -> Topup

    /// `GET /v1/wallet/topup/{topupId}` — what became of a session (Δ C046).
    func topUpState(topupId: String) async throws -> Topup

    /// `POST /v1/vouchers/purchase` — buy a denomination at its tier discount (US-9.19).
    func buyVoucher(denominationMinor: Int64, method: TopupMethod) async throws -> VoucherPurchase
}

/// ``TopUpRepository`` over `:shared`'s typed wallet and subscription clients.
final class ApiTopUpRepository: TopUpRepository {

    private let wallet: WalletApi
    private let subscription: SubscriptionApi

    init(wallet: WalletApi, subscription: SubscriptionApi) {
        self.wallet = wallet
        self.subscription = subscription
    }

    /// **The percentages are DB-configurable and are never baked** (US-9.19,
    /// `billing.voucher_discount_tiers`). `VoucherCatalogue` deliberately ships no default ladder: a
    /// client that has not read the tiers has nothing to sell, which is the honest answer rather than a
    /// rate no admin set.
    ///
    /// **The tiers are validated before Kotlin sees them, and that is not defensive tidying.**
    /// `VoucherCatalogue`'s constructor carries four `require`s — one tier per denomination, a positive
    /// denomination, a discount that is neither negative nor above 100% — and a Kotlin `require` inside
    /// a non-suspend, non-`@Throws` function reaches Swift as an **uncaught Objective-C exception**,
    /// which Swift cannot catch and which terminates the app. The Android twin's `launchGuarded`
    /// catches the identical failure and shows an error, so the parity-preserving answer is to raise
    /// the same refusal as an ordinary Swift `throw`, which the model then renders as copy. A malformed
    /// tier table must be a screen that says so, not a crash on the wallet tab.
    func voucherTiers() async throws -> VoucherCatalogue {
        let tiers = try await wallet.listVoucherDiscountTiers().tiers
        guard Self.isWellFormed(tiers) else { throw WalletContractViolation.malformedVoucherTier }
        return VoucherCatalogue(tiers: tiers)
    }

    /// Whether `VoucherCatalogue`'s constructor would accept `tiers` — its own four `require`s, asked
    /// rather than triggered.
    ///
    /// `static` and `internal` so ``TopUpModelTests`` can put a malformed table through the rule
    /// itself: what the crash-avoidance depends on is that this predicate and Kotlin's `init` agree,
    /// and a test that re-typed the predicate would still pass on the day one of them moved.
    static func isWellFormed(_ tiers: [VoucherDiscountTier]) -> Bool {
        Set(tiers.map(\.denominationMinor)).count == tiers.count
            && tiers.allSatisfy { tier in
                tier.denominationMinor > 0
                    && tier.discountBps >= 0
                    && tier.discountBps <= VoucherCatalogue.companion.FULL_DISCOUNT_BPS
            }
    }

    /// The wallet is credited **only on the gateway callback** (D-09), so the response is a session to
    /// open, never a balance. `TopupRules.routeFor` picks the endpoint so the card/wallet/LankaQR split
    /// lives in `:shared` rather than in a `switch` on this screen.
    func topUp(method: TopupMethod, amountMinor: Int64) async throws -> Topup {
        switch TopupRules.shared.routeFor(method: method) {
        case TopupRoute.lankaqr:
            return try await wallet.topupWithLankaqr(
                request: LankaqrTopupRequest(amountMinor: amountMinor),
                idempotencyKey: nil
            )
        // `onepay`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default:
            return try await wallet.topupWithOnepay(
                request: OnepayTopupRequest(amountMinor: amountMinor, returnUrl: Self.returnUrl),
                idempotencyKey: nil
            )
        }
    }

    /// What a screen polls after coming back from the hosted page: the credit posts on the webhook, so
    /// an app returning from the gateway may arrive before it does, and D6' §7.1 gives the session a
    /// 90-second pending window this is how a driver's screen resolves.
    func topUpState(topupId: String) async throws -> Topup {
        try await wallet.getTopup(topupId: topupId)
    }

    /// The server prices it: this names the denomination and the rail, and the response carries the
    /// `paidMinor`/`creditedMinor` pair the receipt prints. `creditedMinor` always equals the face value
    /// (`ck_voucher_credit_full`) — the discount lives entirely in what was paid.
    func buyVoucher(denominationMinor: Int64, method: TopupMethod) async throws -> VoucherPurchase {
        try await subscription.purchaseVoucher(
            request: PurchaseVoucherRequest(
                denominationMinor: denominationMinor,
                method: method.isOnepay ? VoucherPayMethod.onepay : VoucherPayMethod.lankaqr
            ),
            idempotencyKey: nil
        )
    }

    /// Where OnePay sends the driver back to.
    ///
    /// **Δ iOS, and it is what makes the OnePay arm an in-app browser at all.** The Android twin sends
    /// `returnUrl = null` and lets OnePay use its configured default, because an `ACTION_VIEW` hand-off
    /// comes back through the task stack whatever the page ends on. Here the page is an
    /// `SFSafariViewController` the app itself presents — the cell's own `Δ iOS` clause — and a sheet
    /// has to be told when it is finished: ``SafariView`` watches for a redirect onto
    /// ``PaymentReturn/host`` and dismisses on it. The host is `pay.mageride.lk` because that is the
    /// `applinks:` domain `DriverApp.entitlements` already declares for the payment leg (D2' §C,
    /// *"Payment deep-link: Universal Links → portal"*), so the same URL also reaches the app if the
    /// driver finishes the payment in real Safari instead.
    ///
    /// The redirect is a **shortcut, not a dependency**: a driver who taps **Done** on the sheet
    /// resolves the session through the same poll, which is the path the Android twin has and the only
    /// one that works when the gateway ends on a page of its own.
    private static let returnUrl = PaymentReturn.url
}
