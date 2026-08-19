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
import lk.mageride.passenger.live.LiveEvent
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.onboarding.PhoneNumber
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.LocationRequestState
import kotlin.time.Duration.Companion.seconds

/**
 * The four ways a proxy pickup can be captured (AL-20's addition included).
 *
 * The **wireframe** draws four — Search, Map pin, Paste link, Request loc — and the prompt's fence
 * repeats them. D2' §SCR-PA-010b's own component table still says three; it predates AL-20, and
 * D2' §SCR-PA-012a already names *"010b proxy pickup"* as a paste-link entry point, so the spec
 * disagrees with itself and the wireframe settles it. Recorded in the C079 handoff.
 */
internal enum class PickupMethod { SEARCH, MAP, PASTE_LINK, REQUEST }

/**
 * SCR-PA-010b's state.
 *
 * @property riderRegistered `null` until the number has been looked up. `false` is P-03's
 *   *"Not a MageRide user — enter pickup manually"* (US-8.19) — a real answer, not an error.
 * @property requestState Where the FCM round-trip is (P-02). `null` before one is sent.
 * @property secondsLeft The wireframe's `5:00` countdown, from `CreateLocationRequestResponse.ttl`.
 * @property pickup What the chosen method produced.
 */
internal data class ProxyRiderState(
    val riderName: String = "",
    val riderPhone: String = "",
    val method: PickupMethod = PickupMethod.SEARCH,
    val riderRegistered: Boolean? = null,
    val looking: Boolean = false,
    val requestId: String? = null,
    val requestState: LocationRequestState? = null,
    val secondsLeft: Int = 0,
    val pickup: Place? = null,
    @param:StringRes val error: Int? = null,
) {
    /** Whether the number is a `+947XXXXXXXX` at all — the local gate before any call goes out. */
    val phoneComplete: Boolean get() = PhoneNumber.isValid(riderPhone)

    /**
     * Whether *"Request rider location"* can fire.
     *
     * Blocked while one is already pending — P-12 allows five an hour, and a second tap while the
     * first is still counting down would spend one of them on the same rider.
     */
    val canRequestLocation: Boolean
        get() = phoneComplete && riderRegistered != false && requestState != LocationRequestState.Pending

    /** Whether this screen is done and SCR-PA-009 can book. */
    val isComplete: Boolean get() = riderName.isNotBlank() && phoneComplete && pickup != null

    /** The wireframe's `5:00`. */
    val countdown: String
        get() = "${secondsLeft / SECONDS_PER_MINUTE}:${(secondsLeft % SECONDS_PER_MINUTE).toString().padStart(2, '0')}"

    private companion object {
        const val SECONDS_PER_MINUTE = 60
    }
}

/**
 * SCR-PA-010b — who the ride is actually for.
 *
 * **The location request is a round trip through two other systems and back** (P-02, P-13): this
 * app `POST`s it, ride-svc sends the rider a **silent FCM data message**, the rider answers on
 * SCR-PA-011, and the answer arrives here over **SignalR** in the
 * `booker:{bookerId}:loc-req:{requestId}` group. The socket is the primary channel and the poll is
 * the fallback — see [awaitResolution] — because a booker whose app was backgrounded through the
 * whole five minutes has no socket event to have missed.
 *
 * **P-03 is checked before anything is sent.** `GET /v1/users/lookup` says whether the number
 * belongs to a registered user; if it does not there is nobody to FCM, and the screen says
 * *"Not a MageRide user — enter pickup manually"* (US-8.19) rather than sending a request that
 * expires in silence.
 */
@Suppress("TooManyFunctions") // Two fields, four methods, and the whole P-02 round trip.
internal class ProxyRiderViewModel(
    private val draft: BookingDraft,
    private val bookings: BookingRepository,
    private val live: PassengerLiveMap,
) : ViewModel() {

    private val mutableState = MutableStateFlow(
        ProxyRiderState(riderName = draft.current.riderName, riderPhone = draft.current.riderPhone),
    )

    val state: StateFlow<ProxyRiderState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch {
            live.events.collect { event ->
                if (event is LiveEvent.LocationRequest) {
                    // The Confirmed carries the pin; a Declined carries nothing at all, which is
                    // P-02's privacy rule visible in the payload rather than only in the copy.
                    event.payload.geo?.let { setPickup(Place(lat = it.lat, lng = it.lng)) }
                    onResolved(event.payload.requestId, event.payload.state)
                }
            }
        }
    }

    fun onNameChanged(value: String) {
        mutableState.update { it.copy(riderName = value) }
        draft.update { it.copy(riderName = value) }
    }

    /**
     * A keystroke in the number field, or a pick from the contact list.
     *
     * Normalised on every one through C077's [PhoneNumber], so a phonebook entry stored as
     * `077 123 4567` and one stored as `+94 77 123 4567` are the same rider. The registration
     * answer is dropped whenever the number changes — it was about a different person.
     */
    fun onPhoneChanged(value: String) {
        val normalised = PhoneNumber.normalise(value)
        mutableState.update {
            it.copy(riderPhone = normalised, riderRegistered = null, error = null)
        }
        draft.update { it.copy(riderPhone = normalised) }
        if (PhoneNumber.isValid(normalised)) lookUp(normalised)
    }

    /** The Search / Map pin / Paste link / Request loc segmented control. */
    fun setMethod(method: PickupMethod) {
        mutableState.update { it.copy(method = method, error = null) }
    }

    /** Whatever the chosen method produced — a search result, a dropped pin, or a pasted link. */
    fun setPickup(place: Place) {
        mutableState.update { it.copy(pickup = place) }
        // `pickupIsChosen`, because every one of the four methods produces a place somebody
        // picked — which is what stops SCR-PA-009 printing "Current location" over the rider's
        // pin. See `BookingDraftState.pickupIsChosen`.
        draft.update { it.copy(pickup = place, pickupIsChosen = true) }
    }

    /**
     * *"Request rider location"* — the FCM round trip (P-02).
     *
     * The rider is subscribed to over SignalR **before** the countdown starts, so an answer that
     * arrives in the first second is not missed while this coroutine is still setting up.
     */
    @Suppress("TooGenericExceptionCaught")
    fun requestRiderLocation() {
        val phone = mutableState.value.riderPhone
        if (!mutableState.value.canRequestLocation) return

        viewModelScope.launch {
            try {
                val created = bookings.requestRiderLocation(PhoneNumber.toE164(phone))
                live.watchLocationRequest(created.requestId)
                mutableState.update {
                    it.copy(
                        requestId = created.requestId,
                        requestState = created.state,
                        secondsLeft = created.ttl,
                        error = null,
                    )
                }
                if (created.state == LocationRequestState.RiderNotRegistered) {
                    // P-03 again, this time from ride-svc's own view. The lookup can say
                    // "registered" and the create still refuse — a user deleted between the two.
                    mutableState.update { it.copy(riderRegistered = false) }
                    return@launch
                }
                countDown(created)
                awaitResolution(created.requestId)
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    // ------------------------------------------------------------------------------------------

    /** P-03 — is there anybody to send an FCM to? */
    @Suppress("TooGenericExceptionCaught")
    private fun lookUp(phone: String) {
        mutableState.update { it.copy(looking = true) }
        viewModelScope.launch {
            try {
                val registered = bookings.isRegistered(PhoneNumber.toE164(phone))
                mutableState.update { it.copy(riderRegistered = registered, looking = false) }
                // US-8.19: an unregistered rider cannot be asked, so the screen moves the booker
                // to a method that works rather than leaving a dead button selected.
                if (!registered && mutableState.value.method == PickupMethod.REQUEST) {
                    mutableState.update { it.copy(method = PickupMethod.SEARCH) }
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // Unknown rather than unregistered. Leaving `null` keeps every method available,
                // which is the safe direction: the worst case is a request that expires.
                mutableState.update { it.copy(looking = false) }
            }
        }
    }

    /** The wireframe's `5:00`, ticking. Stops the moment the request resolves. */
    private fun countDown(created: CreateLocationRequestResponse) {
        viewModelScope.launch {
            var left = created.ttl
            while (left > 0 && mutableState.value.requestState == LocationRequestState.Pending) {
                delay(1.seconds)
                left -= 1
                mutableState.update { if (it.requestId == created.requestId) it.copy(secondsLeft = left) else it }
            }
            if (mutableState.value.requestState == LocationRequestState.Pending) {
                onResolved(created.requestId, LocationRequestState.Expired)
            }
        }
    }

    /**
     * The fallback poll.
     *
     * The socket is the primary channel and usually wins. This exists for the booker who
     * backgrounded the app, lost the connection and came back — `PassengerLiveMap` rejoins the
     * group on reconnect but the event that fired while it was away is gone, and
     * `GET /v1/location-requests/{id}` is the contract's own answer to exactly that
     * (*"the poll exists for reconnect and support diagnosis"*).
     */
    private suspend fun awaitResolution(requestId: String) {
        while (mutableState.value.requestState == LocationRequestState.Pending) {
            delay(POLL_INTERVAL)
            pollOnce(requestId)
        }
    }

    /** One read. A failure is not fatal — the socket may still deliver, and the countdown ends this. */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun pollOnce(requestId: String) {
        val answer = try {
            bookings.locationRequest(requestId)
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            return
        }

        if (answer.state == LocationRequestState.Pending) return
        answer.geo?.let { setPickup(Place(lat = it.lat, lng = it.lng)) }
        onResolved(requestId, answer.state)
    }

    /**
     * The request reached a terminal state, from either channel.
     *
     * A **Confirmed** carries the pin, which is what auto-fills the pickup. A **Declined** carries
     * nothing at all and must not be made to look as though it did (P-02): the screen falls back
     * to the other three methods.
     */
    private fun onResolved(requestId: String, state: LocationRequestState) {
        if (mutableState.value.requestId != requestId) return
        mutableState.update { it.copy(requestState = state, secondsLeft = 0) }

        if (state != LocationRequestState.Confirmed && mutableState.value.pickup == null) {
            mutableState.update { it.copy(method = PickupMethod.SEARCH) }
        }
    }

    private companion object {
        /**
         * How often the fallback poll runs.
         *
         * Ten seconds over a five-minute window is thirty reads at worst, and only for a booker
         * whose socket is not delivering. Faster would be polling a channel that already works.
         */
        val POLL_INTERVAL = 10.seconds
    }
}
