package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.domain.fare.FarePaymentAction
import lk.mageride.shared.domain.wallet.TopupMethod
import lk.mageride.shared.domain.wallet.TopupRules
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * **SCR-DA-022 · top up wallet** (US-9.18, US-9.19, AL-05, AL-15).
 *
 * ### Three methods, and there is no fourth
 *
 * Card and OnePay wallet are the same endpoint — the choice between them is made on OnePay's own
 * hosted page — and LankaQR is the other. **Bank transfer is not a top-up method** (AL-05): its
 * route was deleted rather than deprecated, and nothing in this class, [TopUpRepository] or
 * `:shared`'s `TopupMethod` can express one.
 *
 * ### A voucher is a purchase, not a discounted top-up
 *
 * Selecting a tile switches the CTA from *"Pay Rs 2,000"* to *"Pay Rs 1,800 · get Rs 2,000"* and
 * the call from a top-up to `POST /v1/vouchers/purchase`. Topping up the discounted price instead
 * would credit that price on the webhook **and** the face value on the purchase — see
 * [TopUpRepository]'s KDoc. The percentages are the tier table's and are read, never baked
 * (`billing.voucher_discount_tiers` is admin-set per denomination, US-9.19).
 *
 * ### The wallet is credited on the callback, so the screen polls
 *
 * Neither rail credits anything when the app calls it: D-09's entry posts on the gateway webhook.
 * D6' §7.1 gives the session a 90-second pending window, and [resumeFromGateway] is how a driver
 * returning from the hosted page resolves it without guessing. [pollInterval] and [pendingWindow]
 * are injected for the same reason C072's two view models take a clock — a test that could only
 * wait for real time would have to sleep for a minute and a half.
 */
// Nine of the fifteen bodies below are the screen's own controls — three chips, a field, a tile
// row, a CTA and three dismissals — and each is one wireframe affordance. The rest are the gateway
// lifecycle, which is genuinely four steps (start, hand off, poll, settle). Folding any pair
// together would hide a state the driver can be in.
@Suppress("TooManyFunctions")
internal class TopUpViewModel(
    private val topUps: TopUpRepository,
    private val handoff: PaymentHandoff,
    private val pollInterval: Duration = POLL_INTERVAL,
    private val pendingWindow: Duration = PENDING_WINDOW,
) : ViewModel() {

    private val mutableState = MutableStateFlow(TopUpState())

    val state: StateFlow<TopUpState> = mutableState.asStateFlow()

    init {
        loadTiers()
    }

    /** The `💳 Card · OnePay · LankaQR` chips. */
    fun selectMethod(method: TopupMethod) {
        mutableState.update { it.copy(method = method, fallbackQr = null) }
    }

    /**
     * The amount field.
     *
     * Typing clears any selected tile: a voucher is a fixed denomination, and an amount beside a
     * highlighted tile that no longer describes it is how a driver pays for one thing believing
     * they bought another.
     */
    fun onAmountChange(raw: String) {
        mutableState.update { it.copy(amount = WalletInput.rupeeDigits(raw), voucherDenominationMinor = null) }
    }

    /**
     * A voucher tile. Selecting one fills the amount with its **face value**; tapping it again
     * clears the selection and leaves the figure behind as a plain top-up.
     */
    fun selectVoucher(denominationMinor: Long) {
        mutableState.update { current ->
            val cleared = current.voucherDenominationMinor == denominationMinor
            current.copy(
                voucherDenominationMinor = if (cleared) null else denominationMinor,
                amount = WalletInput.rupeesOf(denominationMinor),
            )
        }
    }

    /** Re-reads the tier ladder. */
    fun refresh() {
        loadTiers()
    }

    /**
     * The CTA — start the session and hand off to the gateway.
     *
     * A voucher goes to subscription-svc and a plain amount to wallet-svc; both answer with
     * somewhere to send the driver, and `TopupRules.actionFor` — which reuses the fare side's AL-15
     * rule rather than restating it — decides which.
     */
    fun pay() {
        val current = mutableState.value
        val payable = current.payableMinor?.takeIf { it > 0 } ?: return
        if (current.submitting || current.pending != null) return

        mutableState.update { it.copy(submitting = true, error = null, fallbackQr = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(submitting = false) } }) {
            val denomination = current.voucherDenominationMinor
            if (denomination == null) startTopUp(payable, current.method) else buyVoucher(denomination, current.method)
        }
    }

    /**
     * Called when the screen comes back to the foreground after a gateway hand-off.
     *
     * Polls `GET /v1/wallet/topup/{topupId}` until the session leaves `Pending` or D6' §7.1's
     * window closes.
     */
    fun resumeFromGateway() {
        val pending = mutableState.value.pending?.takeIf { !it.timedOut } ?: return
        if (mutableState.value.submitting) return

        mutableState.update { it.copy(submitting = true) }
        launchGuarded(onFailure = { mutableState.update { it.copy(submitting = false) } }) { poll(pending) }
    }

    /** Dismisses the AL-15 fallback code. */
    fun dismissFallback() {
        mutableState.update { it.copy(fallbackQr = null) }
    }

    /** Dismisses the receipt and clears the form for another top-up. */
    fun dismissReceipt() {
        mutableState.update { it.copy(receipt = null, pending = null, amount = "", voucherDenominationMinor = null) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    private fun loadTiers() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val catalogue = topUps.voucherTiers()
            mutableState.update { it.copy(loading = false, catalogue = catalogue) }
        }
    }

    private suspend fun startTopUp(amountMinor: Long, method: TopupMethod) {
        val topup = topUps.topUp(method, amountMinor)
        val reached = handOff(TopupRules.actionFor(topup, method), topup.qrPayload)

        mutableState.update {
            // Only a session the driver actually reached a gateway for is worth polling.
            it.copy(submitting = false, pending = if (reached) PendingTopUp(topup.topupId, amountMinor) else null)
        }
    }

    private suspend fun buyVoucher(denominationMinor: Long, method: TopupMethod) {
        val purchase = topUps.buyVoucher(denominationMinor, method)
        // `VoucherPurchase` carries a `redirectUrl` and a `qrPayload` and **no LankaQR deep link** —
        // `subscription.yaml` declares no such field — so the LankaQR arm of a voucher is AL-15's
        // fallback by construction. Recorded in the C073 handoff.
        val action = if (method.isOnepay) {
            purchase.redirectUrl?.let(FarePaymentAction::OpenOnepay) ?: FarePaymentAction.Unavailable
        } else {
            purchase.qrPayload?.let(FarePaymentAction::ShowLankaQrFallback) ?: FarePaymentAction.Unavailable
        }
        handOff(action, purchase.qrPayload)

        mutableState.update {
            it.copy(
                submitting = false,
                receipt = TopUpReceipt(
                    paidMinor = purchase.paidMinor,
                    creditedMinor = purchase.creditedMinor,
                    settled = false,
                ),
            )
        }
    }

    /**
     * Sends the driver where [action] says, falling back to the rendered code when the handset
     * could not open the link (AL-15 — see [PaymentHandoff] on why this is tried, not asked).
     *
     * @return whether the driver reached a gateway.
     */
    private fun handOff(action: FarePaymentAction, qrPayload: String?): Boolean = when (action) {
        is FarePaymentAction.OpenOnepay ->
            handoff.open(action.redirectUrl).also { if (!it) fail(R.string.error_gateway_unreachable) }

        is FarePaymentAction.OpenBankApp ->
            handoff.open(action.url).also { if (!it) showFallback(qrPayload) }

        // A code on the screen is not a completed payment, but it *is* a gateway the driver has
        // reached: their bank app scans it and the webhook resolves the session as usual.
        is FarePaymentAction.ShowLankaQrFallback -> showFallback(action.payload)

        else -> {
            fail(R.string.error_gateway_unreachable)
            false
        }
    }

    private fun showFallback(payload: String?): Boolean {
        if (payload.isNullOrBlank()) {
            fail(R.string.error_gateway_unreachable)
            return false
        }
        mutableState.update { it.copy(fallbackQr = payload) }
        return true
    }

    private fun fail(@StringRes message: Int) {
        mutableState.update { it.copy(error = message) }
    }

    private suspend fun poll(pending: PendingTopUp) {
        var waited = Duration.ZERO
        while (waited < pendingWindow) {
            val topup = topUps.topUpState(pending.topupId)
            if (topup.state != TopupState.Pending) {
                settle(topup, pending)
                return
            }
            delay(pollInterval)
            waited += pollInterval
        }
        mutableState.update { it.copy(submitting = false, pending = pending.copy(timedOut = true)) }
    }

    private fun settle(topup: Topup, pending: PendingTopUp) {
        val succeeded = topup.state == TopupState.Succeeded
        mutableState.update { current ->
            current.copy(
                submitting = false,
                pending = null,
                receipt = if (succeeded) {
                    TopUpReceipt(topup.amountMinor, creditedMinor = topup.amountMinor, settled = true)
                } else {
                    null
                },
                // The wireframe's "Failed → retry": the form keeps the figure so the driver taps
                // once rather than entering the amount again.
                error = if (succeeded) null else R.string.wallet_topup_failed,
                amount = if (succeeded) current.amount else WalletInput.rupeesOf(pending.amountMinor),
            )
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    internal companion object {

        /** How often a handed-off session is re-read while the driver waits. */
        val POLL_INTERVAL: Duration = 3.seconds

        /** D6' §7.1's pending window for a gateway session. */
        val PENDING_WINDOW: Duration = 90.seconds
    }
}
