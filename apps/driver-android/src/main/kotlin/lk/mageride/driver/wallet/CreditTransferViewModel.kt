package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.R
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus
import lk.mageride.shared.domain.wallet.CreditTransferIntent
import lk.mageride.shared.domain.wallet.CreditTransferRejection
import lk.mageride.shared.domain.wallet.CreditTransferRules
import lk.mageride.shared.domain.wallet.WalletStanding

/**
 * SCR-DA-024's state.
 *
 * @property incoming Requests waiting on this driver's decision (US-9.11/9.12).
 * @property recipientId The Driver ID typed into the send field.
 * @property amount The rupee digits.
 * @property standing This driver's wallet, for the affordability check.
 * @property history Credit sent and received, newest first (US-9A.11).
 * @property loading The three reads are in flight.
 * @property busyTransferId The request currently being approved or rejected.
 * @property submitting The direct send is in flight.
 * @property sent The row the last successful send produced.
 * @property error Resolved copy for the last failure.
 */
internal data class CreditTransferState(
    val incoming: List<TransferRow> = emptyList(),
    val recipientId: String = "",
    val amount: String = "",
    val standing: WalletStanding? = null,
    val history: List<TransferRow> = emptyList(),
    val loading: Boolean = true,
    val busyTransferId: Ulid? = null,
    val submitting: Boolean = false,
    val sent: TransferRow? = null,
    @param:StringRes val error: Int? = null,
) {

    /** Whether what has been typed is a well-formed platform id. */
    val recipientIdValid: Boolean get() = WalletInput.isDriverId(recipientId)

    /** Whether the field should be drawn in `error`. */
    val recipientIdRejected: Boolean get() = recipientId.isNotBlank() && !recipientIdValid

    /** What is about to move. */
    val amountMinor: Long? get() = WalletInput.amountMinor(amount)

    /**
     * What the sender is debited, and what the recipient is credited: **the same figure** (AL-01).
     *
     * Two properties rather than one so the wireframe's *"You send / Recipient gets"* card reads
     * off the rules that make it true rather than off the same field twice — and so that a change
     * which introduced a commission would have to say so here, in a screen, where it is visible.
     */
    val debited: Money? get() = amountMinor?.let { CreditTransferRules.debitedFromSender(Money.ofMinor(it)) }

    /** @see debited */
    val credited: Money? get() = amountMinor?.let { CreditTransferRules.creditedToRecipient(Money.ofMinor(it)) }
}

/**
 * **SCR-DA-024 · credit transfer + requests** (US-9.11, US-9.12, US-9.20, US-9.21, AL-01).
 *
 * Two halves of one screen: the holder's approval inbox, and the direct send.
 *
 * ### The exact value, and never a commission line
 *
 * `CreditTransferRules.feeFor` is zero for every amount and `entryFor` produces two postings that
 * sum to zero — there is no journal kind a fee could post under, so a commission is not a setting
 * this platform has. The *"You send Rs 1,000 / Recipient gets Rs 1,000"* card is that rule rendered,
 * and it reads both figures off `:shared` rather than printing the input twice.
 *
 * ### Affordability is checked against the **spendable** balance
 *
 * `WalletStanding.available` is the balance net of accrued cancellation debt (D-05), and it is what
 * the daily-fee gate checks too. A driver holding Rs 300 who owes Rs 200 can send Rs 100; offering
 * them Rs 250 would be describing money they do not have. The server checks again at approval time,
 * which is why the balance is re-read after every decision.
 *
 * ### The inbox is read, not pushed
 *
 * D2' says the incoming requests *"arrive via push"* and **no such notification type exists** —
 * `NotificationCatalogue` declares twenty-six and none is a credit transfer, so nothing raises one.
 * The list is therefore read on open and after every action. See [CreditTransferRepository.pending].
 */
// The eight public bodies are the screen's affordances — two fields, approve, reject, send, refresh
// and two dismissals — and each maps to one control the wireframe draws.
@Suppress("TooManyFunctions")
internal class CreditTransferViewModel(
    private val identity: DriverIdentity,
    private val transfers: CreditTransferRepository,
    private val wallet: WalletRepository,
) : ViewModel() {

    private val mutableState = MutableStateFlow(CreditTransferState())

    val state: StateFlow<CreditTransferState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** The recipient's Driver ID field. */
    fun onRecipientIdChange(raw: String) {
        mutableState.update { it.copy(recipientId = raw, error = null) }
    }

    /** The amount field. */
    fun onAmountChange(raw: String) {
        mutableState.update { it.copy(amount = WalletInput.rupeeDigits(raw), error = null) }
    }

    /** Why the send would be refused, or `null` when it would go through. */
    // One guard per thing that is not yet known (no session, no amount, no balance read) plus the
    // one rule this class owns; nesting five of them reads worse than five returns. Same argument
    // `PushRouter.resolve` makes.
    @Suppress("ReturnCount")
    fun rejectionForSend(): CreditTransferRejection? {
        val current = mutableState.value
        val senderId = identity.driverId ?: return null
        val recipientId = WalletInput.driverId(current.recipientId)
        val amount = current.amountMinor?.let(Money::ofMinor) ?: return CreditTransferRejection.NON_POSITIVE_AMOUNT
        val standing = current.standing ?: return null

        // `CreditTransferIntent`'s own `init` refuses a self-transfer and a non-positive amount, so
        // the two are answered before one is built rather than by catching what it throws.
        if (senderId == recipientId) return CreditTransferRejection.SELF_TRANSFER
        return CreditTransferRules.rejectionFor(CreditTransferIntent(senderId, recipientId, amount), standing)
    }

    /** Whether **Send credit** is live. */
    fun canSend(): Boolean {
        val current = mutableState.value
        return !current.submitting && current.recipientIdValid && rejectionForSend() == null
    }

    /** Re-reads the inbox, the balance and the transfer history. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val incoming = transfers.pending()
            val standing = wallet.balance(driverId)
            val history = transfers.history(driverId)
            mutableState.update {
                it.copy(loading = false, incoming = incoming, standing = standing, history = history)
            }
        }
    }

    /**
     * **Approve** — debits this driver, credits the requester, no fee leg (US-9.13).
     *
     * The row is removed from the inbox on the server's answer, not on the tap: a `409` means
     * somebody already answered it and a `402` means the balance moved underneath, and in both
     * cases the request is still there to be looked at.
     */
    fun approve(transferId: Ulid) {
        decide(transferId) { transfers.approve(transferId) }
    }

    /** **Decline** — nothing is posted (US-9.12). */
    fun reject(transferId: Ulid) {
        decide(transferId) { transfers.reject(transferId) }
    }

    /** **Send credit** — the push half, by Driver ID, exact value (US-9.20/9.21). */
    fun send() {
        if (!canSend()) return
        val current = mutableState.value
        val amountMinor = current.amountMinor ?: return
        val recipientId = WalletInput.driverId(current.recipientId)

        mutableState.update { it.copy(submitting = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(submitting = false) } }) {
            val row = transfers.send(recipientDriverId = recipientId, amountMinor = amountMinor)
            mutableState.update { it.copy(submitting = false, sent = row, recipientId = "", amount = "") }
            refresh()
        }
    }

    /** Dismisses the *"sent"* acknowledgement. */
    fun dismissSent() {
        mutableState.update { it.copy(sent = null) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    private fun decide(transferId: Ulid, action: suspend () -> TransferRow) {
        if (mutableState.value.busyTransferId != null) return
        mutableState.update { it.copy(busyTransferId = transferId, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(busyTransferId = null) } }) {
            val row = action()
            mutableState.update { current ->
                current.copy(
                    busyTransferId = null,
                    incoming = current.incoming.filterNot { it.transferId == row.transferId },
                    // An approved request is money that has moved; a rejected one is not, and
                    // putting a REJECTED row into the history list would be wrong on both counts.
                    history = if (row.status == TransferStatus.APPROVED) {
                        listOf(row) + current.history.filterNot { it.transferId == row.transferId }
                    } else {
                        current.history
                    },
                )
            }
            // The balance moved (or was refused against a figure that has since changed), and the
            // next decision is checked against it.
            refreshBalance()
        }
    }

    private fun refreshBalance() {
        val driverId = identity.driverId ?: return
        launchGuarded {
            val standing = wallet.balance(driverId)
            mutableState.update { it.copy(standing = standing) }
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
}

/** Trilingual copy for a refusal the device worked out before spending a round trip. */
@StringRes
internal fun CreditTransferRejection.messageRes(): Int = when (this) {
    CreditTransferRejection.INSUFFICIENT_BALANCE -> R.string.error_insufficient_wallet
    CreditTransferRejection.NON_POSITIVE_AMOUNT -> R.string.wallet_amount_required
    CreditTransferRejection.SELF_TRANSFER -> R.string.wallet_self_transfer
}
