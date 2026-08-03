package lk.mageride.driver.wallet

import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.api.wallet.WalletApi
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.PurchaseVoucherRequest
import lk.mageride.shared.data.models.subscription.VoucherPayMethod
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import lk.mageride.shared.data.models.wallet.LankaqrTopupRequest
import lk.mageride.shared.data.models.wallet.OnepayTopupRequest
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.domain.wallet.TopupMethod
import lk.mageride.shared.domain.wallet.TopupRoute
import lk.mageride.shared.domain.wallet.TopupRules
import lk.mageride.shared.domain.wallet.VoucherCatalogue

/**
 * SCR-DA-022 — the two top-up rails and the bulk-voucher ladder.
 *
 * ### There is no third rail (AL-05)
 *
 * Card and OnePay wallet are **one endpoint**, `POST /v1/wallet/topup/onepay`, because the
 * card-versus-wallet choice is made on OnePay's own hosted page; LankaQR is the other. Bank
 * transfer is not a top-up method and its route was deleted rather than deprecated — `wallet.yaml`
 * carries no `POST /v1/wallet/topup/bank-transfer`, `WalletApi` has no such call, and
 * `MoneyDomainHygieneTest` fails `:shared`'s build if the words reappear in that package. Nothing
 * here may add one.
 *
 * ### Buying a voucher is NOT topping up the discounted price
 *
 * A voucher rides subscription-svc's `POST /v1/vouchers/purchase`, which initiates the gateway
 * payment **and** posts the credit on confirmation. Initiating a `topupWithOnepay(900)` and then
 * calling a purchase would credit Rs 900 on the webhook *and* Rs 1,000 on the purchase — Rs 1,900
 * for a Rs 1,000 voucher. wallet-svc's own `purchaseVoucherFromWallet` takes an already-settled
 * `gatewayRef` and initiates nothing, so it is the wrong half of the pair for a screen: it is the
 * reconciliation entry point, not the buy button. See the C073 handoff.
 */
internal class TopUpRepository(private val wallet: WalletApi, private val subscription: SubscriptionApi) {

    /**
     * `GET /v1/wallet/voucher/discount-tiers` — the denominations on sale and their discounts.
     *
     * **The percentages are DB-configurable and are never baked** (US-9.19, `billing.voucher_discount_tiers`).
     * `VoucherCatalogue` deliberately ships no default ladder: a client that has not read the tiers
     * has nothing to sell, which is the honest answer rather than a rate no admin set.
     */
    suspend fun voucherTiers(): VoucherCatalogue = VoucherCatalogue(wallet.listVoucherDiscountTiers().tiers)

    /**
     * Starts a plain top-up of [amountMinor] on [method].
     *
     * The wallet is credited **only on the gateway callback** (D-09), so the response is a session
     * to open, never a balance. `TopupRules.routeFor` picks the endpoint so the card/wallet/LankaQR
     * split lives in `:shared` rather than in a `when` on this screen.
     */
    suspend fun topUp(method: TopupMethod, amountMinor: Long): Topup = when (TopupRules.routeFor(method)) {
        TopupRoute.ONEPAY -> wallet.topupWithOnepay(OnepayTopupRequest(amountMinor = amountMinor))
        TopupRoute.LANKAQR -> wallet.topupWithLankaqr(LankaqrTopupRequest(amountMinor = amountMinor))
    }

    /**
     * `GET /v1/wallet/topup/{topupId}` — what became of a session (Δ C046).
     *
     * What a screen polls after coming back from the hosted page: the credit posts on the webhook,
     * so an app returning from the gateway may arrive before it does, and D6' §7.1 gives the
     * session a 90-second pending window this is how a driver's screen resolves.
     */
    suspend fun topUpState(topupId: Ulid): Topup = wallet.getTopup(topupId)

    /**
     * `POST /v1/vouchers/purchase` — buy a denomination at its tier discount (US-9.19).
     *
     * The server prices it: this names the denomination and the rail, and the response carries the
     * `paidMinor`/`creditedMinor` pair the receipt prints. `creditedMinor` always equals the face
     * value (`ck_voucher_credit_full`) — the discount lives entirely in what was paid.
     */
    suspend fun buyVoucher(denominationMinor: Long, method: TopupMethod): VoucherPurchase =
        subscription.purchaseVoucher(
            PurchaseVoucherRequest(
                denominationMinor = denominationMinor,
                method = if (method.isOnepay) VoucherPayMethod.ONEPAY else VoucherPayMethod.LANKAQR,
            ),
        )
}
