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
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.wallet.TransferDirectionFilter
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus

/**
 * SCR-DA-023's state.
 *
 * @property holderId The Driver ID typed into the field — the driver being asked.
 * @property amount The rupee digits.
 * @property outgoing Requests this driver has raised that are still awaiting a decision.
 * @property submitting The POST is in flight.
 * @property justRequested The row the last successful request produced — the wireframe's
 *   *"Awaiting driver approval"*.
 * @property error Resolved copy for the last failure.
 */
internal data class RequestCreditState(
    val holderId: String = "",
    val amount: String = "",
    val outgoing: List<TransferRow> = emptyList(),
    val submitting: Boolean = false,
    val justRequested: TransferRow? = null,
    @param:StringRes val error: Int? = null,
) {

    /** Whether what has been typed is a well-formed platform id. Blank is "not yet", not "wrong". */
    val holderIdValid: Boolean get() = WalletInput.isDriverId(holderId)

    /** Whether the field should be drawn in `error` — typed something, and it is not an id. */
    val holderIdRejected: Boolean get() = holderId.isNotBlank() && !holderIdValid

    /** Whether **Request transfer** is live. */
    val canRequest: Boolean
        get() = !submitting && holderIdValid && (WalletInput.amountMinor(amount) ?: 0L) > 0L
}

/**
 * **SCR-DA-023 · request credit** (US-9.10, AL-01, AL-34).
 *
 * The **pull** half of a driver-to-driver transfer: a driver who is short names a holder by Driver
 * ID and an amount, and the holder approves it on SCR-DA-024. Nothing moves here — the POST creates
 * a `PENDING` row and the money is the holder's until they say otherwise.
 *
 * **Driver ID only.** AL-34 removed the QR-scan path this screen used to draw; there is no scanner
 * seam in this class and nothing takes a scanned payload. What the field validates against is the
 * contract's own `Ulid` pattern — see [WalletInput] for why the wireframe's `DRV-22011` is not a
 * thing that exists.
 *
 * **No balance check, deliberately.** A request costs the requester nothing and the holder's
 * balance is not this driver's to see; `402 insufficient-wallet` is raised at *approval* time, on
 * the holder's screen, against the holder's wallet.
 */
internal class RequestCreditViewModel(
    private val identity: DriverIdentity,
    private val transfers: CreditTransferRepository,
) : ViewModel() {

    private val mutableState = MutableStateFlow(RequestCreditState())

    val state: StateFlow<RequestCreditState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** The Driver ID field. */
    fun onHolderIdChange(raw: String) {
        mutableState.update { it.copy(holderId = raw, error = null) }
    }

    /** The amount field. */
    fun onAmountChange(raw: String) {
        mutableState.update { it.copy(amount = WalletInput.rupeeDigits(raw), error = null) }
    }

    /** Re-reads the outstanding requests this driver has raised. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        launchGuarded {
            // A request this driver raised is one they will RECEIVE credit from, which is the side
            // `TransferRow.direction` describes. `PENDING` is the only status worth listing here:
            // an approved one is money already in the wallet and belongs to the history screen.
            val outgoing = transfers.history(driverId, TransferDirectionFilter.RECEIVED)
                .filter { it.status == TransferStatus.PENDING }
            mutableState.update { it.copy(outgoing = outgoing) }
        }
    }

    /** **Request transfer** — creates the `PENDING` row and pushes it onto the outgoing list. */
    fun request() {
        val current = mutableState.value
        if (!current.canRequest) return
        val amountMinor = WalletInput.amountMinor(current.amount) ?: return
        val holderId = WalletInput.driverId(current.holderId)

        mutableState.update { it.copy(submitting = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(submitting = false) } }) {
            val row = transfers.request(holderDriverId = holderId, amountMinor = amountMinor)
            mutableState.update {
                it.copy(
                    submitting = false,
                    justRequested = row,
                    // The form is cleared because the request now lives in the list below it;
                    // leaving it filled invites a second identical ask on the same holder.
                    holderId = "",
                    amount = "",
                    outgoing = listOf(row) + it.outgoing.filterNot { held -> held.transferId == row.transferId },
                )
            }
        }
    }

    /** Dismisses the *"Awaiting driver approval"* acknowledgement. */
    fun dismissAcknowledgement() {
        mutableState.update { it.copy(justRequested = null) }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
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
