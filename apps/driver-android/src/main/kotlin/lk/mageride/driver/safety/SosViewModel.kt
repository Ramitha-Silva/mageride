package lk.mageride.driver.safety

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
import lk.mageride.driver.R
import lk.mageride.driver.location.DriverLocationSource
import lk.mageride.driver.location.Fix
import lk.mageride.driver.location.asPoint
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.driver.profile.ProfileRepository
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.safety.SosSmsStatus
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/** Where SCR-DA-032 is. One at a time — the alarm is raised once and does not go back. */
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
 * SCR-DA-032's state.
 *
 * @property stage Which of the four states the screen draws.
 * @property secondsLeft The auto-send countdown, while [stage] is [SosStage.ARMED].
 * @property contact Who the SMS goes to (AL-13). `null` means nobody is on file.
 * @property contactLoaded Whether the emergency-contact read has answered yet.
 * @property position The driver's own last fix — the coordinate the alert carries.
 * @property smsStatus What D-33's parallel gateways managed, once dispatched.
 * @property error Resolved copy for a failure to reach safety-svc at all.
 */
internal data class SosState(
    val stage: SosStage = SosStage.ARMED,
    val secondsLeft: Int = SosViewModel.COUNTDOWN_SECONDS,
    val contact: EmergencyContact? = null,
    val contactLoaded: Boolean = false,
    val position: Fix? = null,
    val smsStatus: SosSmsStatus? = null,
    @param:StringRes val error: Int? = null,
) {

    /** Whether the alarm has been raised. Sticky — nothing takes the screen back out of it. */
    val isRaised: Boolean get() = stage == SosStage.DISPATCHED

    /**
     * Whether the AL-13 warning is drawn — *"no emergency contact, so the SMS has nobody to go to"*.
     *
     * Shown **before** the alarm rather than instead of it: `POST /v1/sos` still records the event
     * and still raises the admin live feed with no contact on file, and refusing to send would take
     * away the half that does work. safety-svc answers `SosSmsStatus.NO_CONTACT` for the other half.
     */
    val warnsNoContact: Boolean get() = contactLoaded && contact == null

    /**
     * Whether the screen is still waiting for a fix to attach to the alarm.
     *
     * **`POST /v1/sos` has no positionless form**: `TriggerSosRequest.lat`/`.lng` are required, so
     * there is no request to make until the handset has answered once. BR-29.4 contemplates exactly
     * this case for the *web* surface — *"geolocation denied → SOS still fires with the last known
     * driver-reported position"* — and the app-facing contract carries no equivalent. Recorded as a
     * spec gap in the C075 handoff. In practice this is milliseconds: `DriverLocationSource` emits
     * the **last known** fix before it registers for updates.
     */
    val awaitingPosition: Boolean get() = stage == SosStage.ARMED && position == null
}

/**
 * **SCR-DA-032 · the driver SOS** (US-12.8, AL-13, D-33).
 *
 * **Active trip only.** D2' §SCR-DA-032 scopes the driver's alarm to a trip in progress, which is
 * why the route carries a ride id and this class takes one: the SMS and the admin live feed both
 * say *which* trip, and `safety.sos_events.ride_id` is what an operator opens.
 *
 * **The countdown is a cancel window, not a delay.** D-33 budgets **p99 ≤ 5 s** from the request to
 * the SMS leaving both gateways, so seconds spent before the request are seconds taken off
 * somebody's help. Three of them buys back the mis-tap — the disc is the largest control in the app
 * and it is pressed by someone who is not looking — and [raise] sends immediately when the driver
 * taps rather than waiting for the timer they interrupted.
 *
 * **A failed SMS is not a failed SOS.** `SosSmsStatus.FAILED` means the alert **is** recorded and
 * **is** on the admin live feed and the SMS leg did not manage it; the screen says exactly that.
 * Only a request that never reached safety-svc is [SosStage.FAILED], and that one offers a retry.
 */
internal class SosViewModel(
    private val rideId: Ulid,
    private val contact: RideContact,
    private val profiles: ProfileRepository,
    private val location: DriverLocationSource,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SosState())

    val state: StateFlow<SosState> = mutableState.asStateFlow()

    private var countdown: Job? = null

    init {
        readContact()
        observePosition()
    }

    /**
     * The wireframe's SOS disc — raise the alarm now.
     *
     * Idempotent from the driver's side: a second tap while the request is in flight or after it
     * has been answered does nothing, because there is one alarm per trip and a second `POST` would
     * be a second row on the operator's feed for the same emergency.
     *
     * The fix used is the **last known** one, never a fresh read: waiting for a GPS lock inside
     * D-33's five-second budget is how an alarm arrives after the moment it was needed.
     * `DriverLocationSource` has been emitting since the screen opened and the foreground service
     * since the ride began, so the position is already in hand — see [SosState.awaitingPosition]
     * for the one case where it is not.
     */
    fun raise() {
        if (mutableState.value.stage != SosStage.ARMED) return

        countdown?.cancel()
        val at = mutableState.value.position?.asPoint()
        if (at == null) {
            mutableState.update { it.copy(stage = SosStage.FAILED, error = R.string.sos_no_position) }
            return
        }

        mutableState.update { it.copy(stage = SosStage.SENDING, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(stage = SosStage.FAILED) } }) {
            val dispatched = contact.triggerSos(rideId, at)
            mutableState.update {
                it.copy(stage = SosStage.DISPATCHED, smsStatus = dispatched.smsStatus)
            }
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

    /** `GET /v1/me/emergency-contacts` — the row the wireframe draws under the disc (AL-13). */
    private fun readContact() {
        viewModelScope.launch {
            val primary = runCatching { profiles.emergencyContacts() }.getOrNull()
                // "exactly one per account that has any" — the primary is what D-33's fast path
                // denormalises onto `iam.users`, so it is the one the SMS will actually reach.
                ?.let { all -> all.firstOrNull(EmergencyContact::isPrimary) ?: all.firstOrNull() }

            mutableState.update { it.copy(contact = primary, contactLoaded = true) }
        }
    }

    /**
     * The driver's own position, and the countdown that waits for it.
     *
     * The window starts on the **first fix** rather than on composition, because an alarm that
     * fired by itself with no coordinate to carry would have nothing to send (see
     * [SosState.awaitingPosition]). The first emission is the last-known fix, so on any handset
     * that has ever had one this is the same instant the screen appeared.
     */
    private fun observePosition() {
        viewModelScope.launch {
            location.fixes.collect { fix ->
                val first = mutableState.value.position == null
                mutableState.update { it.copy(position = fix) }
                if (first && mutableState.value.stage == SosStage.ARMED) startCountdown()
            }
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

        /**
         * Three seconds of cancel window.
         *
         * Not a spec number — D5' §14.3 fixes the **dispatch** budget (p99 ≤ 5 s) and says nothing
         * about a confirmation. Three is what is left of a five-second sense of urgency after a
         * mis-tap has to be recoverable; anything longer starts spending the budget the SLO is
         * about. Recorded in the C075 handoff.
         */
        const val COUNTDOWN_SECONDS: Int = 3

        val TICK: Duration = 1.seconds
    }
}
