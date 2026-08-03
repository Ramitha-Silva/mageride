package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.wallet.TopupMethod
import lk.mageride.shared.domain.wallet.VoucherCatalogue
import lk.mageride.shared.domain.wallet.VoucherQuote

/**
 * A top-up session the driver has been sent to a gateway for.
 *
 * @property topupId What `GET /v1/wallet/topup/{topupId}` is polled with.
 * @property amountMinor What was asked for, so a failed session can refill the form.
 * @property timedOut D6' §7.1's 90-second window closed with the session still `Pending`. **Not a
 *   failure** — the webhook may simply be late, and telling a driver who has paid that nothing
 *   happened is worse than saying the credit is on its way.
 */
internal data class PendingTopUp(val topupId: Ulid, val amountMinor: Long, val timedOut: Boolean = false)

/**
 * What SCR-DA-022 shows once money has moved, or has been asked to.
 *
 * @property paidMinor What the driver paid.
 * @property creditedMinor What the wallet receives. Equal to [paidMinor] for a plain top-up; the
 *   **face value** for a voucher, which is the whole of US-9.19's discount
 *   (`ck_voucher_credit_full` — the wallet is always credited the denomination).
 * @property settled Whether the credit has already posted. A polled session that reached
 *   `Succeeded` has; a voucher purchase has **not** — subscription-svc posts it on the gateway's
 *   confirmation and exposes no read to poll, so the receipt says so rather than implying a
 *   balance that is not there yet.
 */
internal data class TopUpReceipt(val paidMinor: Long, val creditedMinor: Long, val settled: Boolean)

/**
 * SCR-DA-022's state.
 *
 * @property method Card / OnePay wallet / LankaQR. **Three, and the list is closed** (AL-05).
 * @property amount The rupee digits in the field.
 * @property catalogue The voucher tiers on sale; `null` until they are read.
 * @property voucherDenominationMinor Which tile is selected, or `null` for a plain top-up.
 * @property loading The tier read is in flight — the wireframe's shimmer.
 * @property submitting A gateway call is in flight, or a session is being polled.
 * @property pending A session the driver has been handed off to.
 * @property receipt The success state.
 * @property fallbackQr AL-15's rendered payload, shown only when a deep link could not open.
 * @property error Resolved copy for the last failure.
 */
internal data class TopUpState(
    val method: TopupMethod = TopupMethod.ONEPAY_CARD,
    val amount: String = "",
    val catalogue: VoucherCatalogue? = null,
    val voucherDenominationMinor: Long? = null,
    val loading: Boolean = true,
    val submitting: Boolean = false,
    val pending: PendingTopUp? = null,
    val receipt: TopUpReceipt? = null,
    val fallbackQr: String? = null,
    @param:StringRes val error: Int? = null,
) {

    /** The denominations a driver may buy right now, cheapest first. */
    val vouchers: List<VoucherQuote> get() = catalogue?.onSale.orEmpty()

    /** The selected tile's quote, or `null` when this is a plain top-up. */
    val quote: VoucherQuote? get() = voucherDenominationMinor?.let { catalogue?.quote(it) }

    /** What the driver is about to pay — the tier's price, or the amount they typed. */
    val payableMinor: Long? get() = quote?.price?.amountMinor ?: WalletInput.amountMinor(amount)

    /** What lands in the wallet — the face value for a voucher, the same figure otherwise. */
    val creditedMinor: Long? get() = quote?.credited?.amountMinor ?: payableMinor

    /** Whether the CTA is live. */
    val canPay: Boolean get() = !submitting && pending == null && (payableMinor ?: 0L) > 0L

    /** Whether the screen is waiting on a webhook the driver cannot see — the wireframe's spinner. */
    val awaitingGateway: Boolean get() = pending?.timedOut == false
}
