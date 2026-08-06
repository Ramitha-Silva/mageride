package lk.mageride.passenger.safety

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.passenger.R
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.ride.RideRepository
import lk.mageride.passenger.settings.SosContacts
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.safety.SosSmsStatus
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/** Where SCR-PA-029 is. One at a time — the alarm is raised once and does not go back. */
internal enum class SosStage {

    /** The wireframe's big red disc, with the countdown running under it. */
    ARMED,

    /** `POST /v1/sos` is in flight. D-33's five-second budget is measured across this. */
    SENDING,

    /** safety-svc answered. The alert is recorded and on the admin live feed. */
    DISPATCHED,

    /** The call did not reach safety-svc at all. Nothing was raised, and the screen says so. */
    FAILED,
}

/**
 * SCR-PA-029's state.
 *
 * @property stage Which of the four states the screen draws.
 * @property secondsLeft The auto-send countdown, while [stage] is [SosStage.ARMED].
 * @property contacts Who is on file (AL-13, US-12.1). Empty means nobody.
 * @property contactsLoaded Whether the emergency-contact read has answered yet.
 * @property position The passenger's own last fix — the coordinate the alert carries.
 * @property smsStatus What D-33's parallel gateways managed, once dispatched.
 * @property shareLink D-34's live trip link, minted after the alarm has gone.
 * @property error Resolved copy for a failure to reach safety-svc at all.
 */
internal data class SosState(
    val stage: SosStage = SosStage.ARMED,
    val secondsLeft: Int = SosViewModel.COUNTDOWN_SECONDS,
    val contacts: List<EmergencyContact> = emptyList(),
    val contactsLoaded: Boolean = false,
    val position: PassengerFix? = null,
    val smsStatus: SosSmsStatus? = null,
    val shareLink: String? = null,
    @param:StringRes val error: Int? = null,
) {

    /** Whether the alarm has been raised. Sticky — nothing takes the screen back out of it. */
    val isRaised: Boolean get() = stage == SosStage.DISPATCHED

    /**
     * The contact D-33's fast path will actually reach.
     *
     * iam-svc promotes exactly one onto `iam.users.emergency_contact_name/phone` because the SLO is
     * p99 ≤ 5 s and a join is not in it — and the app never sets `isPrimary` itself (see
     * `SosContacts`). The rest of the list is drawn so the passenger can see who is on file; only
     * this one wears the `Sent` pill, because only this one is sent to.
     */
    val primaryContact: EmergencyContact?
        get() = contacts.firstOrNull(EmergencyContact::isPrimary) ?: contacts.firstOrNull()

    /**
     * Whether the AL-13 warning is drawn — *"nobody is on file, so the SMS has nowhere to go"*.
     *
     * Shown **before** the alarm rather than instead of it. Unlike the driver's SCR-DA-032 this is
     * not merely informational: with `Safety:RequireEmergencyContact` at its default,
     * `POST /v1/sos` answers `400 no-emergency-contact` and **nothing at all is raised** — so a
     * passenger with an empty list is told here, while there is still time to add one on
     * SCR-PA-027b, and the disc still tries because the setting can be off and because refusing
     * locally would be this app deciding an outcome the platform owns.
     */
    val warnsNoContact: Boolean get() = contactsLoaded && contacts.isEmpty()

    /**
     * Whether the screen is still waiting for a fix to attach to the alarm.
     *
     * **`POST /v1/sos` has no positionless form**: `TriggerSosRequest.lat`/`.lng` are required, so
     * there is no request to make until the handset has answered once. BR-29.4 contemplates exactly
     * this case for the *web* surface — *"geolocation denied → SOS still fires with the last known
     * driver-reported position"* — and the app-facing contract carries no equivalent. In practice
     * this is milliseconds: `PassengerLocationSource` emits the **last known** fix before it
     * registers for updates.
     */
    val awaitingPosition: Boolean get() = stage == SosStage.ARMED && position == null
}

/**
 * **SCR-PA-029 · the passenger SOS** (US-12.1, AL-13, D-33, D-34).
 *
 * **Trip-scoped, because the wireframe's only door is SCR-PA-015's `⛨ SOS`.** The screen's own copy
 * is *"Sending GPS + trip to emergency contacts"* and `safety.sos_events.ride_id` is what an
 * operator opens, so the route carries a ride id and this class takes one. `POST /v1/sos` marks
 * `rideId` optional and would permit a trip-less alarm; no wireframe draws an entry point for one.
 *
 * **The countdown is a cancel window, not a delay.** D-33 budgets **p99 ≤ 5 s** from the request to
 * the SMS leaving both gateways, so seconds spent before the request are seconds taken off
 * somebody's help. Three of them buys back the mis-tap — the disc is the largest control on the
 * screen and it is pressed by someone who is not looking — and [raise] sends immediately when the
 * passenger taps rather than waiting for the timer they interrupted.
 *
 * **A failed SMS is not a failed SOS.** `SosSmsStatus.FAILED` means the alert **is** recorded and
 * **is** on the admin live feed and the SMS leg did not manage it; the screen says exactly that.
 * Only a request that never reached safety-svc is [SosStage.FAILED], and that one offers a retry.
 *
 * **The share link is minted after the alarm, never before it.** D-34's `POST /v1/trip-share/{id}`
 * is a second round trip, and putting it in front of `POST /v1/sos` would spend the five-second
 * budget on a link. It is also allowed to fail: an alarm that went out with no link to hand on is
 * still an alarm that went out.
 */
internal class SosViewModel(
    private val rideId: Ulid,
    private val rides: RideRepository,
    private val contacts: SosContacts,
    private val locations: PassengerLocationSource,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SosState())

    val state: StateFlow<SosState> = mutableState.asStateFlow()

    private var countdown: Job? = null

    init {
        readContacts()
        observePosition()
    }

    /**
     * The wireframe's SOS disc — raise the alarm now.
     *
     * Idempotent from the passenger's side: a second tap while the request is in flight or after it
     * has been answered does nothing, because there is one alarm per trip and a second `POST` would
     * be a second row on the operator's feed for the same emergency.
     *
     * The fix used is the **last known** one, never a fresh read: waiting for a GPS lock inside
     * D-33's five-second budget is how an alarm arrives after the moment it was needed.
     */
    fun raise() {
        if (mutableState.value.stage != SosStage.ARMED) return

        countdown?.cancel()
        val at = mutableState.value.position
        if (at == null) {
            mutableState.update { it.copy(stage = SosStage.FAILED, error = R.string.sos_no_position) }
            return
        }

        mutableState.update { it.copy(stage = SosStage.SENDING, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(stage = SosStage.FAILED) } }) {
            val dispatched = rides.triggerSos(rideId, at.lat, at.lng)
            mutableState.update { it.copy(stage = SosStage.DISPATCHED, smsStatus = dispatched.smsStatus) }
            mintShareLink()
        }
    }

    /** Puts the countdown back after a failure, so the disc is live again. */
    fun retry() {
        if (mutableState.value.stage != SosStage.FAILED) return
        mutableState.update { it.copy(stage = SosStage.ARMED, secondsLeft = COUNTDOWN_SECONDS, error = null) }
        // Still nothing to send with, so nothing to count down to; the fix collector starts the
        // window when the handset answers.
        if (mutableState.value.position != null) startCountdown()
    }

    /** **Cancel** — stops the auto-send. Only reachable before the alarm has gone. */
    fun cancelCountdown() {
        countdown?.cancel()
        countdown = null
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /** `GET /v1/me/emergency-contacts` — the rows the wireframe draws under the disc (AL-13). */
    private fun readContacts() {
        viewModelScope.launch {
            val all = runCatching { contacts.list() }.getOrDefault(emptyList())
            mutableState.update { it.copy(contacts = all, contactsLoaded = true) }
        }
    }

    /**
     * The passenger's own position, and the countdown that waits for it.
     *
     * The window starts on the **first fix** rather than on composition, because an alarm that
     * fired by itself with no coordinate to carry would have nothing to send (see
     * [SosState.awaitingPosition]). The first emission is the last-known fix, so on any handset
     * that has ever had one this is the same instant the screen appeared.
     */
    private fun observePosition() {
        viewModelScope.launch {
            locations.fixes.collect { fix ->
                val first = mutableState.value.position == null
                mutableState.update { it.copy(position = fix) }
                if (first && mutableState.value.stage == SosStage.ARMED) startCountdown()
            }
        }
    }

    /**
     * D-34's live trip link, once the alarm is out.
     *
     * Best-effort by construction: it is minted **after** the state is already `DISPATCHED`, and a
     * failure leaves the alarm exactly where it is. `409 ride-terminal` is the ordinary case for a
     * trip that ended while the screen was open — there is nothing left to follow, and saying so
     * would be noise on top of an alarm that did go out.
     */
    @Suppress("TooGenericExceptionCaught") // The link is an extra; its failure must not reach the screen.
    private suspend fun mintShareLink() {
        try {
            val link = rides.shareTrip(rideId)
            mutableState.update { it.copy(shareLink = link.url) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // See the KDoc.
        }
    }

    /** The cancel window. Ticks down to zero and then raises the alarm by itself. */
    private fun startCountdown() {
        countdown?.cancel()
        countdown = viewModelScope.launch {
            while (isActive && mutableState.value.secondsLeft > 0) {
                delay(TICK)
                mutableState.update { it.copy(secondsLeft = it.secondsLeft - 1) }
            }
            if (isActive) raise()
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the passenger raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = SafetyErrors.messageFor(cause)) }
            }
        }
    }

    internal companion object {

        /**
         * Three seconds of cancel window.
         *
         * Not a spec number — D5' §14.3 fixes the **dispatch** budget (p99 ≤ 5 s) and says nothing
         * about a confirmation. Three is what is left of a five-second sense of urgency after a
         * mis-tap has to be recoverable; anything longer starts spending the budget the SLO is
         * about. The driver's SCR-DA-032 took the same three, and one platform should not have two
         * answers to *"how long do I have to cancel"*.
         */
        const val COUNTDOWN_SECONDS: Int = 3

        val TICK: Duration = 1.seconds
    }
}
