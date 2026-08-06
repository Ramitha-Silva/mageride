package lk.mageride.passenger.ride

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.fare.FareBreakdown
import lk.mageride.shared.data.models.ride.RideDetail

/**
 * SCR-PA-018's state.
 *
 * @property breakdown The fare's parts — first km, per km, surcharges. **Read-only detail**: US-8.4
 *   says only the total is shown while booking, and this is the receipt afterwards, which is a
 *   different question.
 * @property settled Whether the money is done. `false` puts a *"Pay now"* CTA on the screen.
 */
internal data class TripSummaryState(
    val ride: RideDetail? = null,
    val breakdown: FareBreakdown? = null,
    val settled: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** The total, as the hero line. */
    val totalMinor: Long? get() = ride?.fare?.amountMinor

    /** Whether SCR-PA-019 should be offered — a completed ride with a driver to rate. */
    val canRate: Boolean get() = ride?.driver != null
}

/**
 * SCR-PA-018 — the receipt.
 *
 * **No tip control, and that is the wireframe's own instruction**: its state line reads *"(drop tip
 * / India charges)"*. `InitiatePaymentRequest.tipMinor` exists and E-10 defines the journal kind,
 * so the contract is ready whenever the product decision reverses — but a tip row nobody asked for
 * on a receipt is a charge a passenger has to notice and decline.
 *
 * **The breakdown is not re-derived here.** `FareEstimate` carries the total and `FareBreakdown`
 * carries the parts; multiplying a per-km rate by a distance on the device would produce a second,
 * disagreeing number the moment a rounding rule changed server-side.
 */
internal class TripSummaryViewModel(private val rideId: String, private val rides: RideRepository) : ViewModel() {

    private val mutableState = MutableStateFlow(TripSummaryState())

    val state: StateFlow<TripSummaryState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { load() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val ride = rides.ride(rideId)
            mutableState.update { it.copy(ride = ride, settled = ride.state.isSettled(), error = null) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
        }
    }
}

/**
 * Whether the fare has been dealt with.
 *
 * `CashSettled` counts: the driver took the money in the vehicle, which is a settled ride even
 * though nothing moved through the platform. `PaymentPending` does not, which is what puts the
 * *"Pay now"* CTA on the receipt.
 */
private fun RideState.isSettled(): Boolean =
    this == RideState.Paid || this == RideState.CashSettled || this == RideState.CashOnDeliveryCollected
