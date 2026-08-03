package lk.mageride.driver.home

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.domain.dispatch.OfferOutcome
import lk.mageride.shared.domain.dispatch.OfferSession
import lk.mageride.shared.domain.dispatch.OfferSessionState
import lk.mageride.shared.domain.dispatch.RideOffer
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-DA-014's state.
 *
 * @property offer The live offer, or `null` when the takeover is not up.
 * @property detail The ride behind it, once the enrichment read has landed. `null` is a real state
 *   — the countdown runs from the first frame and the badges appear when they appear.
 * @property remaining What is left of the fifteen seconds, for the ring.
 * @property deciding An accept or a decline is in flight.
 * @property outcome How it ended, for the screen to act on once.
 */
internal data class OfferUiState(
    val offer: RideOffer? = null,
    val detail: RideDetail? = null,
    val remaining: Duration = RideOffer.TTL,
    val deciding: Boolean = false,
    val outcome: OfferOutcome? = null,
) {

    /** Whether the full-screen takeover is showing. */
    val isLive: Boolean get() = offer != null

    /** The last five seconds — the wireframe's *"last 5s pulse red"*. */
    val isUrgent: Boolean get() = remaining <= URGENT

    private companion object {

        /** D2' §SCR-DA-014: *"counting-down (ring shrinks, last 5s pulse red)"*. */
        val URGENT: Duration = 5.seconds
    }
}

/**
 * **SCR-DA-014 · the fifteen-second offer** (US-6A.2/6A.3, R-02).
 *
 * The decision itself is `:shared`'s — [OfferSession] holds the driver's single slot, refuses to
 * send an accept whose deadline has already gone, and keeps `409 offer-already-accepted`
 * ([OfferOutcome.Taken]) apart from `410 offer-expired` ([OfferOutcome.Expired]) all the way out.
 * What this view model adds is the three things a screen needs and a domain object should not have:
 *
 * * **The countdown drives the ring**, and reaching zero **returns the driver to standby**. It is
 *   `OfferSession.countdown()`, which completes at zero and derives what is left from the
 *   *deadline* rather than from when this device saw the push — a push that took two seconds shows
 *   thirteen seconds of ring.
 * * **The enrichment read.** `offer.created` carries an id, a deadline and a rendered fare
 *   (see [OfferInbox]); the proxy badge, the package size, the pickup and the drop are on the ride.
 *   One `GET /v1/rides/{rideId}` inside the window, and its `version` is handed straight to
 *   [OfferSession.onVersionKnown] so the accept does not have to spend a second round trip on
 *   `GET …/state` (R-14).
 * * **The outcome is consumed once.** Winning navigates to SCR-DA-015; every other ending puts the
 *   driver back on the standby map. A rotation must not do either twice.
 *
 * @param tick How often the ring is re-rendered. A parameter so a test can run a countdown to its
 *   end without spending fifteen real seconds on it.
 */
internal class OfferViewModel(
    private val offers: OfferSession,
    private val rides: ActiveRideRepository,
    private val tick: Duration = COUNTDOWN_INTERVAL,
) : ViewModel() {

    private val mutableState = MutableStateFlow(OfferUiState())

    val state: StateFlow<OfferUiState> = mutableState.asStateFlow()

    private var countdownJob: Job? = null

    init {
        viewModelScope.launch {
            offers.state.collect(::onSessionState)
        }
    }

    /** The wireframe's **Accept ▸** — the atomic single-winner accept (R-02). */
    fun accept() {
        mutableState.update { it.copy(deciding = true) }
        viewModelScope.launch {
            val outcome = offers.accept()
            mutableState.update { it.copy(deciding = false, outcome = outcome) }
        }
    }

    /** The wireframe's **Reject**. No penalty (D5' §7); dispatch cascades to the next candidate. */
    fun reject() {
        mutableState.update { it.copy(deciding = true) }
        viewModelScope.launch {
            val outcome = offers.decline()
            mutableState.update { it.copy(deciding = false, outcome = outcome) }
        }
    }

    /** Clears the ending once the screen has navigated or dismissed on it. */
    fun consumeOutcome() {
        mutableState.update { it.copy(outcome = null) }
    }

    private fun onSessionState(session: OfferSessionState) {
        when (session) {
            is OfferSessionState.Live -> onLive(session.offer)

            is OfferSessionState.Deciding -> mutableState.update { it.copy(deciding = true) }

            // Won is reported through the accept's own outcome; the slot going Idle after a
            // decline, an expiry or a loss is what takes the takeover down.
            is OfferSessionState.Won, OfferSessionState.Idle -> {
                countdownJob?.cancel()
                mutableState.update { it.copy(offer = null, detail = null, deciding = false) }
            }
        }
    }

    private fun onLive(offer: RideOffer) {
        val alreadyShowing = mutableState.value.offer?.offerId == offer.offerId
        mutableState.update { it.copy(offer = offer, detail = if (alreadyShowing) it.detail else null) }
        if (alreadyShowing) return

        enrich(offer)
        countdownJob?.cancel()
        countdownJob = viewModelScope.launch {
            offers.countdown(tick).collect { left -> mutableState.update { it.copy(remaining = left) } }

            // The flow completed, which means the deadline passed. The server has already released
            // the driver, so declining now would be a `410` for nothing — the slot is simply
            // dropped and the screen falls back to the standby map.
            if (offers.offer?.offerId == offer.offerId) {
                offers.onExpired()
                mutableState.update { it.copy(outcome = OfferOutcome.Expired) }
            }
        }
    }

    /**
     * Reads the ride behind the offer, and tells the session the version it found.
     *
     * Failure is silent on purpose: a driver has fifteen seconds and an offer they can still accept
     * with no badges is worth more than an error over a countdown. The accept falls back to
     * `GET /v1/rides/{rideId}/state` for its version, which is what `OfferSession` does with no
     * version in hand.
     */
    @Suppress("TooGenericExceptionCaught") // A missing badge must never cost the driver the ride.
    private fun enrich(offer: RideOffer) {
        viewModelScope.launch {
            val detail = try {
                rides.detail(offer.rideId)
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                null
            }

            if (detail != null && mutableState.value.offer?.offerId == offer.offerId) {
                offers.onVersionKnown(detail.version)
                mutableState.update { it.copy(detail = detail) }
            }
        }
    }

    internal companion object {

        /** Fine enough that a fifteen-second ring does not visibly step. */
        val COUNTDOWN_INTERVAL: Duration = 250.milliseconds
    }
}
