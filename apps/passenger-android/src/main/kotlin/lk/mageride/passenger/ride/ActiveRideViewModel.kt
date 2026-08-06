package lk.mageride.passenger.ride

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
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.ride.CancellationPenalty
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import kotlin.time.Clock
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-014 and SCR-PA-015's shared state.
 *
 * @property ride The aggregate. `null` until the first read lands.
 * @property driverPosition The assigned driver's live marker (US-6A.12), from the SignalR plane.
 * @property secondsUntilTimeout SCR-PA-014's `1:34 left`. Zero once the two minutes are up.
 * @property noDriver The 2-minute timeout fired (US-6A.11) — *"No drivers available"* + retry.
 * @property pendingCancel The confirm dialog is open, holding what cancelling will cost.
 * @property penalty D-05's Rs 50, once a cancel has actually incurred one.
 * @property shareLink The share-trip URL, once minted.
 */
internal data class ActiveRideState(
    val ride: RideDetail? = null,
    val driverPosition: GeoPoint? = null,
    val secondsUntilTimeout: Int = 0,
    val noDriver: Boolean = false,
    val cancelling: Boolean = false,
    val pendingCancel: Boolean = false,
    val penalty: CancellationPenalty? = null,
    val cancelled: Boolean = false,
    val shareLink: String? = null,
    val sosSent: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** SCR-PA-014's `1:34`. */
    val countdown: String
        get() = "${secondsUntilTimeout / SECONDS_PER_MINUTE}:" +
            (secondsUntilTimeout % SECONDS_PER_MINUTE).toString().padStart(2, '0')

    /** Whether a driver has been assigned — the line between SCR-PA-014 and SCR-PA-015. */
    val assigned: Boolean get() = ride?.driver != null

    /**
     * Whether cancelling now costs the passenger anything (US-6A.9 vs US-6A.10).
     *
     * Free before acceptance, Rs 50 after — and the dialog says which **before** the tap, because
     * a fee a passenger discovers afterwards is a fee they will contest.
     */
    val cancelIsFree: Boolean
        get() = ride?.state?.let { it == RideState.Requested || it == RideState.Matching || it == RideState.Offered }
            ?: true

    /** The driver's real number, from acceptance onward (AL-48). `null` before that. */
    val driverPhone: String? get() = ride?.counterpartyPhone

    private companion object {
        const val SECONDS_PER_MINUTE = 60
    }
}

/**
 * SCR-PA-014 and SCR-PA-015 — one ride, two faces.
 *
 * They are one view model because they are one aggregate: *"Finding a driver…"* and *"Driver
 * arriving · 3 min"* are `Matching` and `Accepted` on the same row, and splitting them would mean
 * two subscriptions to the same SignalR group and two polls of the same endpoint. The **screens**
 * are separate because the wireframe draws them separately and the back stack treats them
 * differently — SCR-PA-014 is replaced by SCR-PA-015, never stacked under it.
 *
 * **The socket is the channel and the poll is the floor.** `RideStateChanged` and `DriverMoved`
 * arrive over the plane C076 owns; the poll exists because a ride is the one thing in this app a
 * passenger cannot afford to be wrong about, and because the two-minute timeout has to fire even
 * on a handset whose socket died in a tunnel.
 *
 * **Cancelling after acceptance costs Rs 50 and the passenger is told so first.** D-05 carries the
 * debt to the *next* trip rather than charging now, which is exactly why it has to be said out
 * loud: nothing appears on a statement today, and a passenger who was not warned will meet it
 * three weeks later as a surprise.
 */
@Suppress("TooManyFunctions") // Two screens' worth of actions on one aggregate — see the KDoc.
internal class ActiveRideViewModel(
    private val rideId: String,
    private val rides: RideRepository,
    private val live: PassengerLiveMap,
    private val now: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(ActiveRideState())

    val state: StateFlow<ActiveRideState> = mutableState.asStateFlow()

    private val startedAt = now()

    init {
        live.watchRide(rideId)
        viewModelScope.launch { refresh() }
        viewModelScope.launch { watchEvents() }
        viewModelScope.launch { poll() }
        viewModelScope.launch { countDown() }
    }

    override fun onCleared() {
        live.stopWatchingRide(rideId)
        super.onCleared()
    }

    /** Re-reads the aggregate. */
    @Suppress("TooGenericExceptionCaught")
    fun refresh() {
        viewModelScope.launch {
            try {
                val detail = rides.ride(rideId)
                mutableState.update { it.copy(ride = detail, error = null, noDriver = detail.noDriver()) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** The `✕` was tapped. Opens the confirm dialog rather than cancelling. */
    fun askToCancel() {
        mutableState.update { it.copy(pendingCancel = true) }
    }

    fun dismissCancel() {
        mutableState.update { it.copy(pendingCancel = false) }
    }

    /**
     * Confirmed — `POST /v1/rides/{rideId}/cancel`.
     *
     * The response's `penalty` is kept in state so the screen can say what was incurred: D-05
     * settles on the next trip, so this is the only moment the passenger is told a number.
     */
    @Suppress("TooGenericExceptionCaught", "ReturnCount")
    fun confirmCancel() {
        val current = mutableState.value
        val ride = current.ride ?: return
        if (current.cancelling) return

        mutableState.update { it.copy(cancelling = true, pendingCancel = false, error = null) }
        viewModelScope.launch {
            try {
                val answer = rides.cancel(rideId, ride.version, RideCancelReason.RIDER_CHANGED_MIND)
                mutableState.update {
                    it.copy(cancelling = false, cancelled = true, penalty = answer.penalty)
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(cancelling = false, error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** The `⛨ SOS` button — D-33's parallel SMS fan-out, with the last known position. */
    @Suppress("TooGenericExceptionCaught")
    fun triggerSos(at: GeoPoint?) {
        val point = at ?: mutableState.value.driverPosition ?: mutableState.value.ride?.pickup?.point ?: return
        viewModelScope.launch {
            try {
                rides.triggerSos(rideId, point.lat, point.lng)
                mutableState.update { it.copy(sosSent = true, error = null) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** Share trip — mints a `safety.trip_share_tokens` link for the passenger to send on. */
    @Suppress("TooGenericExceptionCaught")
    fun shareTrip() {
        viewModelScope.launch {
            try {
                mutableState.update { it.copy(shareLink = rides.shareTrip(rideId).url, error = null) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = RideErrors.messageFor(cause)) }
            }
        }
    }

    /** The share sheet has been shown. */
    fun onShareConsumed() {
        mutableState.update { it.copy(shareLink = null) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    // ------------------------------------------------------------------------------------------

    /** The socket. `RideStateChanged` re-reads; `DriverMoved` just moves the marker. */
    private suspend fun watchEvents() {
        live.events.collect { event ->
            when (event) {
                is LiveEvent.RideState -> if (event.payload.rideId == rideId) refresh()

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

    /**
     * The floor under the socket.
     *
     * Stops once the ride is terminal — a completed ride does not change again, and a screen that
     * kept polling one would be a background request every few seconds for as long as the app
     * lived.
     */
    private suspend fun poll() {
        while (mutableState.value.ride?.state?.isTerminalForPassenger() != true) {
            delay(POLL_INTERVAL)
            refresh()
        }
    }

    /**
     * US-6A.11's two minutes.
     *
     * Client-side, and deliberately: the timeout is a **UI promise** — *"usually under 2 min"* —
     * and dispatch's own expiry is its business. What this drives is when the screen stops saying
     * "finding" and starts offering a retry, which has to happen even if nothing arrives at all.
     */
    private suspend fun countDown() {
        while (!mutableState.value.assigned && !mutableState.value.cancelled) {
            val elapsed = now() - startedAt
            val left = (SEARCH_WINDOW - elapsed).inWholeSeconds.toInt().coerceAtLeast(0)
            mutableState.update { it.copy(secondsUntilTimeout = left, noDriver = it.noDriver || left == 0) }
            if (left == 0) return
            delay(1.seconds)
        }
    }

    private companion object {
        /** US-6A.11 — *"usually under 2 min"*, and the point at which the screen offers a retry. */
        val SEARCH_WINDOW = 2.minutes

        /** Slower than the socket by design: this is the floor, not the channel. */
        val POLL_INTERVAL = 10.seconds
    }
}

/** Whether dispatch has given up — the ride expired with nobody assigned. */
private fun RideDetail.noDriver(): Boolean = state == RideState.ExpiredNoDriver

/**
 * Whether the passenger's active-ride screen has nothing left to watch.
 *
 * Payment states are **not** terminal here: `Completed` and `PaymentPending` are exactly when
 * SCR-PA-016 and SCR-PA-017 take over, and the screen has to see the transition to hand off.
 */
private fun RideState.isTerminalForPassenger(): Boolean = when (this) {
    RideState.Paid,
    RideState.CashSettled,
    RideState.CashOnDeliveryCollected,
    RideState.CancelledByRiderBeforeAccept,
    RideState.CancelledByRiderAfterAccept,
    RideState.CancelledByDriver,
    RideState.ExpiredNoDriver,
    RideState.NoShowRider,
    RideState.NoShowDriver,
    -> true

    else -> false
}
