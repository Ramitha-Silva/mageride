import Foundation
import MageRideShared

/// A top-up session the driver has been sent to a gateway for.
///
/// - Parameters:
///   - topupId: What `GET /v1/wallet/topup/{topupId}` is polled with.
///   - amountMinor: What was asked for, so a failed session can refill the form.
///   - hasTimedOut: D6' §7.1's 90-second window closed with the session still `Pending`. **Not a
///     failure** — the webhook may simply be late, and telling a driver who has paid that nothing
///     happened is worse than saying the credit is on its way.
struct PendingTopUp: Equatable {

    let topupId: String
    let amountMinor: Int64
    var hasTimedOut = false
}

/// What SCR-DI-022 shows once money has moved, or has been asked to.
///
/// - Parameters:
///   - paidMinor: What the driver pays.
///   - creditedMinor: What the wallet receives. Equal to `paidMinor` for a plain top-up; the **face
///     value** for a voucher, which is the whole of US-9.19's discount (`ck_voucher_credit_full` — the
///     wallet is always credited the denomination).
///   - isSettled: Whether the credit has already posted. A polled session that reached `Succeeded`
///     has; a voucher purchase has **not** — subscription-svc posts it on the gateway's confirmation
///     and exposes no read to poll, so the receipt says so rather than implying a balance that is not
///     there yet.
struct TopUpReceipt: Equatable {

    let paidMinor: Int64
    let creditedMinor: Int64
    let isSettled: Bool
}

/// SCR-DI-022's state.
///
/// - Parameters:
///   - method: Card / OnePay wallet / LankaQR. **Three, and the list is closed** (AL-05).
///   - amount: The rupee digits in the field.
///   - catalogue: The voucher tiers on sale; `nil` until they are read.
///   - voucherDenominationMinor: Which tile is selected, or `nil` for a plain top-up.
///   - isLoading: The tier read is in flight.
///   - isSubmitting: A gateway call is in flight, or a session is being polled.
///   - pending: A session the driver has been handed off to.
///   - receipt: The success state.
///   - onepayUrl: OnePay's hosted page, while the in-app browser is up (Δ iOS).
///   - fallbackQr: AL-15's payload, shown only when a bank app could not open the link.
///   - errorKey: Resolved copy for the last failure.
struct TopUpState {

    var method = TopupMethod.onepayCard
    var amount = ""
    var catalogue: VoucherCatalogue?
    var voucherDenominationMinor: Int64?
    var isLoading = true
    var isSubmitting = false
    var pending: PendingTopUp?
    var receipt: TopUpReceipt?
    var onepayUrl: OnepayCheckout?
    var fallbackQr: LankaQrPayload?
    var errorKey: String?

    /// The denominations a driver may buy right now, cheapest first.
    var vouchers: [VoucherQuote] { catalogue?.onSale ?? [] }

    /// The selected tile's quote, or `nil` when this is a plain top-up.
    var quote: VoucherQuote? {
        voucherDenominationMinor.flatMap { catalogue?.quote(denominationMinor: $0) }
    }

    /// What the driver is about to pay — the tier's price, or the amount they typed.
    var payableMinor: Int64? { quote?.price.amountMinor ?? WalletInput.amountMinor(amount) }

    /// What lands in the wallet — the face value for a voucher, the same figure otherwise.
    var creditedMinor: Int64? { quote?.credited.amountMinor ?? payableMinor }

    /// Whether the CTA is live.
    var canPay: Bool { !isSubmitting && pending == nil && (payableMinor ?? 0) > 0 }

    /// Whether the screen is waiting on a webhook the driver cannot see.
    var isAwaitingGateway: Bool { pending?.hasTimedOut == false }
}

/// OnePay's hosted page, as something `.sheet(item:)` can present.
///
/// `Identifiable` rather than a bare `String` plus a `Bool` for ``ActivityView``'s reason: the pair can
/// disagree for one frame, and the frame it disagrees on is the one where the browser is up on the
/// previous session's checkout.
struct OnepayCheckout: Identifiable, Equatable {

    let url: URL

    var id: String { url.absoluteString }
}

/// AL-15's rendered payload, as something `.sheet(item:)` can present.
struct LankaQrPayload: Identifiable, Equatable {

    let payload: String

    var id: String { payload }
}

/// **SCR-DI-022 · top up wallet** (US-9.18, US-9.19, AL-05, AL-15).
///
/// ### Three methods, and there is no fourth
///
/// Card and OnePay wallet are the same endpoint — the choice between them is made on OnePay's own
/// hosted page — and LankaQR is the other. **Bank transfer is not a top-up method** (AL-05): its route
/// was deleted rather than deprecated, and nothing in this class, ``TopUpRepository`` or `:shared`'s
/// `TopupMethod` can express one.
///
/// ### A voucher is a purchase, not a discounted top-up
///
/// Selecting a tile switches the CTA from *"Pay Rs 2,000"* to *"Pay Rs 1,800 · get Rs 2,000"* and the
/// call from a top-up to `POST /v1/vouchers/purchase`. Topping up the discounted price instead would
/// credit that price on the webhook **and** the face value on the purchase — see ``TopUpRepository``'s
/// own note. The percentages are the tier table's and are read, never baked
/// (`billing.voucher_discount_tiers` is admin-set per denomination, US-9.19).
///
/// ### The wallet is credited on the callback, so the screen polls
///
/// Neither rail credits anything when the app calls it: D-09's entry posts on the gateway webhook.
/// D6' §7.1 gives the session a 90-second pending window, and ``resumeFromGateway()`` is how a driver
/// coming back from the hosted page resolves it without guessing. The poll interval and the window are
/// injected for the reason ``JobBoardModel``'s clock is — a test that could only wait for real time
/// would have to sleep for a minute and a half.
///
/// **Δ iOS — the two rails leave the app by different doors.** OnePay is an `SFSafariViewController`
/// this model *asks the screen to present* (``TopUpState/onepayUrl``), which is the cell's own `Δ iOS`
/// clause and which removes the "no app could open the payment page" failure the Android twin has to
/// report. LankaQR is the one hand-off that really leaves, and it goes through ``PaymentHandoff``.
@MainActor
final class TopUpModel: ObservableObject {

    @Published private(set) var state = TopUpState()

    private let topUps: TopUpRepository
    private let handoff: PaymentHandoff
    private let pollSeconds: TimeInterval
    private let pendingWindowSeconds: TimeInterval

    /// - Parameters:
    ///   - pollSeconds: How often a handed-off session is re-read while the driver waits.
    ///   - pendingWindowSeconds: D6' §7.1's pending window for a gateway session.
    init(
        topUps: TopUpRepository,
        handoff: PaymentHandoff,
        pollSeconds: TimeInterval = TopUpModel.defaultPollSeconds,
        pendingWindowSeconds: TimeInterval = TopUpModel.defaultPendingWindowSeconds
    ) {
        self.topUps = topUps
        self.handoff = handoff
        self.pollSeconds = pollSeconds
        self.pendingWindowSeconds = pendingWindowSeconds
    }

    // MARK: - The form

    /// The `Card · OnePay · LankaQR` segments.
    func select(method: TopupMethod) {
        state.method = method
        state.fallbackQr = nil
    }

    /// The amount field.
    ///
    /// Typing clears any selected tile: a voucher is a fixed denomination, and an amount beside a
    /// highlighted tile that no longer describes it is how a driver pays for one thing believing they
    /// bought another.
    func onAmountChange(_ raw: String) {
        state.amount = WalletInput.rupeeDigits(raw)
        state.voucherDenominationMinor = nil
    }

    /// A voucher tile. Selecting one fills the amount with its **face value**; tapping it again clears
    /// the selection and leaves the figure behind as a plain top-up.
    func selectVoucher(denominationMinor: Int64) {
        let isCleared = state.voucherDenominationMinor == denominationMinor
        state.voucherDenominationMinor = isCleared ? nil : denominationMinor
        state.amount = WalletInput.rupeesOf(denominationMinor)
    }

    /// Re-reads the tier ladder.
    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            state.catalogue = try await topUps.voucherTiers()
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    // MARK: - The gateway

    /// The CTA — start the session and hand off to the gateway.
    ///
    /// A voucher goes to subscription-svc and a plain amount to wallet-svc; both answer with somewhere
    /// to send the driver, and `TopupRules.actionFor` — which reuses the fare side's AL-15 rule rather
    /// than restating it — decides which.
    func pay() async {
        guard state.canPay, let payable = state.payableMinor, payable > 0 else { return }
        let method = state.method
        let denomination = state.voucherDenominationMinor

        state.isSubmitting = true
        state.errorKey = nil
        state.fallbackQr = nil

        do {
            if let denomination {
                try await buyVoucher(denominationMinor: denomination, method: method)
            } else {
                try await startTopUp(amountMinor: payable, method: method)
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isSubmitting = false
    }

    /// Called when the driver comes back from a gateway — the OnePay sheet closing, or the app
    /// returning to the foreground after a bank app.
    ///
    /// Polls `GET /v1/wallet/topup/{topupId}` until the session leaves `Pending` or D6' §7.1's window
    /// closes.
    func resumeFromGateway() async {
        guard let pending = state.pending, !pending.hasTimedOut, !state.isSubmitting else { return }
        state.isSubmitting = true

        do {
            try await poll(pending)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isSubmitting = false
    }

    /// The OnePay sheet was dismissed — by the driver's **Done** or by the return redirect.
    func onCheckoutDismissed() async {
        state.onepayUrl = nil
        await resumeFromGateway()
    }

    // MARK: - Dismissals

    /// Dismisses the AL-15 fallback code.
    func dismissFallback() {
        state.fallbackQr = nil
    }

    /// Dismisses the receipt and clears the form for another top-up.
    func dismissReceipt() {
        state.receipt = nil
        state.pending = nil
        state.amount = ""
        state.voucherDenominationMinor = nil
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    // MARK: -

    private func startTopUp(amountMinor: Int64, method: TopupMethod) async throws {
        let topup = try await topUps.topUp(method: method, amountMinor: amountMinor)
        let action = TopupRules.shared.actionFor(topup: topup, method: method, bankAppAvailable: true)
        let reached = await handOff(action, qrPayload: topup.qrPayload)

        // Only a session the driver actually reached a gateway for is worth polling.
        state.pending = reached ? PendingTopUp(topupId: topup.topupId, amountMinor: amountMinor) : nil
    }

    private func buyVoucher(denominationMinor: Int64, method: TopupMethod) async throws {
        let purchase = try await topUps.buyVoucher(denominationMinor: denominationMinor, method: method)

        // `VoucherPurchase` carries a `redirectUrl` and a `qrPayload` and **no LankaQR deep link** —
        // `subscription.yaml` declares no such field — so the LankaQR arm of a voucher is AL-15's
        // fallback by construction, where a plain top-up on the same rail gets the bank-app link.
        // Recorded in the C073 handoff and carried forward.
        let action: FarePaymentAction
        if method.isOnepay {
            if let redirect = purchase.redirectUrl {
                action = FarePaymentActionOpenOnepay(redirectUrl: redirect)
            } else {
                action = FarePaymentActionUnavailable.shared
            }
        } else if let payload = purchase.qrPayload {
            action = FarePaymentActionShowLankaQrFallback(payload: payload)
        } else {
            action = FarePaymentActionUnavailable.shared
        }
        _ = await handOff(action, qrPayload: purchase.qrPayload)

        state.receipt = TopUpReceipt(
            paidMinor: purchase.paidMinor,
            creditedMinor: purchase.creditedMinor,
            isSettled: false
        )
    }

    /// Sends the driver where `action` says, falling back to the rendered code when no bank app could
    /// open the link (AL-15 — see ``PaymentHandoff`` on why this is tried, not asked).
    ///
    /// - Returns: whether the driver reached a gateway.
    private func handOff(_ action: FarePaymentAction, qrPayload: String?) async -> Bool {
        switch action {
        case let onepay as FarePaymentActionOpenOnepay:
            // The one arm that cannot fail on this platform: the browser is in the app.
            guard let url = URL(string: onepay.redirectUrl) else {
                state.errorKey = "error_gateway_unreachable"
                return false
            }
            state.onepayUrl = OnepayCheckout(url: url)
            return true

        case let bank as FarePaymentActionOpenBankApp:
            if await handoff.openBankApp(bank.url) { return true }
            return showFallback(qrPayload)

        // A code on the screen is not a completed payment, but it *is* a gateway the driver has
        // reached: their bank app scans it and the webhook resolves the session as usual.
        case let fallback as FarePaymentActionShowLankaQrFallback:
            return showFallback(fallback.payload)

        default:
            state.errorKey = "error_gateway_unreachable"
            return false
        }
    }

    private func showFallback(_ payload: String?) -> Bool {
        guard let payload, !payload.isEmpty else {
            state.errorKey = "error_gateway_unreachable"
            return false
        }
        state.fallbackQr = LankaQrPayload(payload: payload)
        return true
    }

    /// Re-reads the session until it leaves `Pending` or the window closes.
    ///
    /// The wait is `try?` and the loop checks `Task.isCancelled`, which is this target's shape for a
    /// poll (``ActiveRideModel``, ``JobBoardModel``): a driver who leaves the screen mid-window has
    /// cancelled the read, not failed it, and a `CancellationError` rendered as copy would put an
    /// error banner on a screen nobody is looking at.
    private func poll(_ pending: PendingTopUp) async throws {
        var waited: TimeInterval = 0
        while waited < pendingWindowSeconds {
            if Task.isCancelled { return }
            let topup = try await topUps.topUpState(topupId: pending.topupId)
            if topup.state != TopupState.pending {
                settle(topup, pending: pending)
                return
            }
            try? await Task.sleep(nanoseconds: UInt64(pollSeconds * Double(NSEC_PER_SEC)))
            if Task.isCancelled { return }
            waited += pollSeconds
        }
        state.pending = PendingTopUp(topupId: pending.topupId, amountMinor: pending.amountMinor, hasTimedOut: true)
    }

    private func settle(_ topup: Topup, pending: PendingTopUp) {
        let succeeded = topup.state == TopupState.succeeded
        state.pending = nil
        state.receipt = succeeded
            ? TopUpReceipt(paidMinor: topup.amountMinor, creditedMinor: topup.amountMinor, isSettled: true)
            : nil
        state.errorKey = succeeded ? nil : "wallet_topup_failed"
        // The wireframe's "Failed → retry": the form keeps the figure so the driver taps once rather
        // than entering the amount again.
        if !succeeded { state.amount = WalletInput.rupeesOf(pending.amountMinor) }
    }

    /// How often a handed-off session is re-read while the driver waits.
    ///
    /// The same three seconds `TopUpViewModel.POLL_INTERVAL` uses on Android.
    static let defaultPollSeconds: TimeInterval = 3

    /// D6' §7.1's pending window for a gateway session.
    static let defaultPendingWindowSeconds: TimeInterval = 90
}
