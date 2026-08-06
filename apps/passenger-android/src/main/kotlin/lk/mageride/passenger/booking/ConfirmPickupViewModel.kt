package lk.mageride.passenger.booking

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
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.ride.LocationRequestState
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-011's state.
 *
 * @property pin Where the rider says they are. Seeded from their own fix and **dragged** from
 *   there — the wireframe's *"drag to adjust"*.
 * @property accuracyMetres The fix's accuracy, sent with a Share. It is what tells dispatch how
 *   much to trust the point; dropping it would make a 500 m cell-tower fix look like a GPS lock.
 * @property secondsLeft The five-minute TTL, counting down. `0` auto-dismisses.
 * @property outcome Set once, terminally. The screen closes on it.
 */
internal data class ConfirmPickupState(
    val bookerName: String? = null,
    val pin: GeoPoint? = null,
    val accuracyMetres: Double = 0.0,
    val secondsLeft: Int = 0,
    val sending: Boolean = false,
    val outcome: LocationRequestState? = null,
    @param:StringRes val error: Int? = null,
) {
    /** The wireframe's `4:38`. */
    val countdown: String
        get() = "${secondsLeft / SECONDS_PER_MINUTE}:${(secondsLeft % SECONDS_PER_MINUTE).toString().padStart(2, '0')}"

    /** Whether Share can fire — there has to be a point to share. */
    val canShare: Boolean get() = pin != null && !sending && outcome == null

    private companion object {
        const val SECONDS_PER_MINUTE = 60
    }
}

/**
 * SCR-PA-011 — the **rider's** side, and the one screen in this app where privacy is the feature.
 *
 * **Declining sends no coordinates. None.** P-02 says so, `ride.yaml` enforces it — the decline
 * operation takes no body at all — and this class makes it structural: [decline] calls
 * [BookingRepository.declineLocationRequest], which has no parameter to put a point in. There is no
 * "approximate location" consolation, no city-level fallback, and the banner says so out loud
 * before the rider decides.
 *
 * **The rider is not necessarily a passenger of this app's booker.** They received a silent FCM
 * data message (`{kind:'location_request', requestId, bookerName, ttl}` — no deeplink, see
 * `PushRouter`), and this screen is all they see of the booking. That is why the booker's name is
 * on it: agreeing to share your position with "somebody" is not consent.
 */
internal class ConfirmPickupViewModel(
    private val requestId: String,
    private val bookings: BookingRepository,
    private val locations: PassengerLocationSource,
    private val now: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(ConfirmPickupState())

    val state: StateFlow<ConfirmPickupState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { load() }
        viewModelScope.launch {
            // The rider's own fix seeds the pin. They can drag it from there, which is what the
            // wireframe's "drag to adjust" is for — a GPS lock indoors is often a building away.
            locations.fixes.collect { fix ->
                mutableState.update {
                    if (it.pin == null) {
                        it.copy(pin = GeoPoint(fix.lat, fix.lng), accuracyMetres = fix.accuracyMetres)
                    } else {
                        it.copy(accuracyMetres = fix.accuracyMetres)
                    }
                }
            }
        }
    }

    /** The pin was dragged. */
    fun onPinMoved(point: GeoPoint) {
        mutableState.update { it.copy(pin = point) }
    }

    /**
     * *"Share location ▸"*.
     *
     * The accuracy travels with the point because `GeoPointWithAccuracy` has a field for it and
     * dispatch uses it: a 500 m cell-tower fix and a 5 m GPS lock are different instructions to a
     * driver, and sending only the coordinate would make them look identical.
     */
    @Suppress("TooGenericExceptionCaught")
    fun share() {
        val current = mutableState.value
        val pin = current.pin ?: return
        if (!current.canShare) return

        mutableState.update { it.copy(sending = true, error = null) }
        viewModelScope.launch {
            try {
                val answer = bookings.confirmLocationRequest(
                    requestId,
                    GeoPointWithAccuracy(
                        lat = pin.lat,
                        lng = pin.lng,
                        accuracy = current.accuracyMetres.takeIf { it > 0 },
                    ),
                )
                mutableState.update { it.copy(sending = false, outcome = answer.state) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(sending = false, error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    /**
     * *"Decline"* — and nothing else leaves the device.
     *
     * Note what is **not** here: no `pin`, no `accuracyMetres`, no last-known anything. The state is
     * not even read. That is the point of P-02, and it is why declining is a separate operation
     * rather than a confirm with a flag.
     */
    @Suppress("TooGenericExceptionCaught")
    fun decline() {
        if (mutableState.value.outcome != null) return
        mutableState.update { it.copy(sending = true, error = null) }
        viewModelScope.launch {
            try {
                val answer = bookings.declineLocationRequest(requestId)
                mutableState.update { it.copy(sending = false, outcome = answer.state) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // The refusal still stands locally: the screen closes and no position was sent.
                // A rider must never be left on a "share" screen because a decline failed — and
                // nothing about the failure changes what was (not) sent.
                mutableState.update {
                    it.copy(sending = false, outcome = LocationRequestState.Declined, error = null)
                }
            }
        }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    // ------------------------------------------------------------------------------------------

    /**
     * Reads the request, and starts the clock from **its** expiry rather than from a fresh 300 s.
     *
     * The FCM may have sat in a Doze bucket for two minutes before the handset woke up; a countdown
     * that started at 5:00 on open would promise time the server has already spent, and the rider
     * would tap Share into a `410`.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val request = bookings.locationRequest(requestId)
            val remaining = (request.expiresAt - now()).inWholeSeconds.toInt().coerceAtLeast(0)
            mutableState.update {
                it.copy(
                    secondsLeft = remaining,
                    outcome = request.state.takeIf { state -> state != LocationRequestState.Pending },
                )
            }
            if (mutableState.value.outcome == null) countDown()
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = BookingErrors.messageFor(cause)) }
        }
    }

    /** The banner's `4:38`. On zero the request is gone and the screen dismisses itself. */
    private fun countDown() {
        viewModelScope.launch {
            while (mutableState.value.secondsLeft > 0 && mutableState.value.outcome == null) {
                delay(1.seconds)
                mutableState.update { it.copy(secondsLeft = (it.secondsLeft - 1).coerceAtLeast(0)) }
            }
            if (mutableState.value.outcome == null) {
                mutableState.update { it.copy(outcome = LocationRequestState.Expired) }
            }
        }
    }
}
