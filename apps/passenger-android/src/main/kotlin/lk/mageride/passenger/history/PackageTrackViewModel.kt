package lk.mageride.passenger.history

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
import lk.mageride.passenger.live.LiveEvent
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.ride.RideErrors
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.data.models.ride.RideDetail
import kotlin.time.Duration.Companion.seconds

/**
 * Which end of the parcel this handset is.
 *
 * **Decided from the ride, not from the URI** — `mageride://package/{rideId}` is the same link for
 * both parties, and which screen to draw is a fact about who is holding the phone: the sender is
 * the ride's booker, the recipient is the number on `recipientPhone`. `PassengerRoute`'s own KDoc
 * says C081 makes this call.
 */
internal enum class PackageParty { SENDER, RECIPIENT }

/**
 * SCR-PA-020 and SCR-PA-021's state.
 *
 * @property otp The code this party reads out — pickup for a sender, delivery for a recipient.
 *   `null` when the app was never told it; see [PackageOtps] for why there is no read.
 * @property status The four-step bar. `null` before the first read.
 */
internal data class PackageTrackState(
    val ride: RideDetail? = null,
    val party: PackageParty = PackageParty.SENDER,
    val status: PackageStatus? = null,
    val driverPosition: GeoPoint? = null,
    val otp: String? = null,
    @param:StringRes val error: Int? = null,
) {
    /**
     * The wireframe's `Pickup pending → Picked up → In transit → Delivered`, as an index.
     *
     * `null` reads as the first step rather than as "unknown": before the driver has confirmed
     * anything, a pickup **is** pending, and an empty bar would look like a screen that failed.
     */
    val step: Int get() = STEPS.indexOf(status ?: PackageStatus.PickupPending)

    internal companion object {
        /** US-20.7's four, in order. The bar and this list cannot disagree because it is one list. */
        val STEPS: List<PackageStatus> = listOf(
            PackageStatus.PickupPending,
            PackageStatus.PickedUp,
            PackageStatus.InTransit,
            PackageStatus.Delivered,
        )
    }

    /** Whether the handover is done and there is nothing left to read out. */
    val delivered: Boolean get() = status == PackageStatus.Delivered
}

/**
 * SCR-PA-020 / SCR-PA-021 — a parcel, from both ends.
 *
 * **One view model for two screens because it is one ride.** The sender and the recipient watch the
 * same `rides.rides` row through the same `ride:{rideId}` SignalR group; what differs is which OTP
 * is theirs to read out and what the header says. Two view models would be two subscriptions to one
 * group.
 *
 * **The recipient never logged in to get here** (P-09, AL-45). They arrived from an FCM deep link
 * on `package_picked_up`, or — with no app — from an SMS onto the SCR-WT web page, which is not
 * this app's problem. Nothing on this screen is reachable by signing in and looking for it.
 */
internal class PackageTrackViewModel(
    private val rideId: String,
    private val history: HistoryRepository,
    private val live: PassengerLiveMap,
    private val otps: PackageOtps,
    private val isSender: (RideDetail) -> Boolean,
) : ViewModel() {

    private val mutableState = MutableStateFlow(PackageTrackState())

    val state: StateFlow<PackageTrackState> = mutableState.asStateFlow()

    init {
        live.watchRide(rideId)
        viewModelScope.launch { load() }
        viewModelScope.launch { watchEvents() }
        viewModelScope.launch { poll() }
    }

    override fun onCleared() {
        live.stopWatchingRide(rideId)
        super.onCleared()
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val ride = history.ride(rideId)
            val party = if (isSender(ride)) PackageParty.SENDER else PackageParty.RECIPIENT
            mutableState.update {
                it.copy(
                    ride = ride,
                    party = party,
                    status = ride.packageStatus,
                    // Each party reads out their own code and never the other's: the pickup OTP
                    // proves the driver collected from the right sender, the delivery OTP proves
                    // they handed it to the right recipient (P-07, US-20.4/20.5).
                    otp = when (party) {
                        PackageParty.SENDER -> otps.pickupFor(rideId)
                        PackageParty.RECIPIENT -> otps.deliveryFor(rideId)
                    },
                    error = null,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
        }
    }

    /** The socket. `PackageStatusChanged` is US-20.7's four-step bar moving. */
    private suspend fun watchEvents() {
        live.events.collect { event ->
            when (event) {
                is LiveEvent.PackageStatus ->
                    if (event.payload.rideId == rideId) {
                        mutableState.update { it.copy(status = event.payload.status) }
                    }

                is LiveEvent.DriverMoved ->
                    if (event.payload.rideId == rideId) {
                        mutableState.update {
                            it.copy(driverPosition = GeoPoint(event.payload.lat, event.payload.lng))
                        }
                    }

                else -> Unit
            }
        }
    }

    /** The floor under the socket, and it stops once the parcel has been handed over. */
    private suspend fun poll() {
        while (!mutableState.value.delivered) {
            delay(POLL_INTERVAL)
            load()
        }
    }

    private companion object {
        val POLL_INTERVAL = 15.seconds
    }
}
