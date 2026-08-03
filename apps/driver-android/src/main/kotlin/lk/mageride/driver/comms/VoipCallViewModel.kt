package lk.mageride.driver.comms

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
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.driver.ride.RideContact
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.CallOutcome
import lk.mageride.shared.data.models.comms.CalleeRole
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/** Where SCR-DA-031 is. At most one at a time, which is why it is an enum rather than four flags. */
internal enum class CallStage {

    /** The token is being minted, or the room is being joined. */
    CONNECTING,

    /** Media is flowing. The timer runs from the moment this is entered. */
    CONNECTED,

    /** The call could not be carried. AL-48's *"Call normally instead?"* is offered from here. */
    FAILED,
}

/**
 * SCR-DA-031's state.
 *
 * @property stage Which of the wireframe's three states is drawn.
 * @property calleeName Who is being called — *"Nimal (rider)"*. `null` until the ride is read.
 * @property counterpartyPhone The rider's real number (P-05), which AL-48's fallback dials.
 * @property failure Why the call failed, which decides the copy under the failure.
 * @property seconds How long the call has been connected, for `Connected · 00:42`.
 * @property muted The 🔇 toggle.
 * @property speakerOn The 🔊 toggle.
 * @property dialNumber A number the screen should hand to the platform dialler, once.
 * @property finished The call is over and the screen should be popped.
 */
internal data class VoipCallState(
    val stage: CallStage = CallStage.CONNECTING,
    val calleeName: String? = null,
    val counterpartyPhone: String? = null,
    val failure: VoipFailure? = null,
    val seconds: Long = 0,
    val muted: Boolean = false,
    val speakerOn: Boolean = false,
    val dialNumber: String? = null,
    val finished: Boolean = false,
) {

    /**
     * Whether *"Call normally instead?"* is offered (AL-48, US-26.4).
     *
     * A failed call that has a number to fall back to. A ride that has ended answers
     * `409 ride-terminal` and carries no `counterpartyPhone`, so this stays false: there is nobody
     * left to reach, and offering a dial would be wrong advice rather than a fallback.
     */
    val canDialDirectly: Boolean get() = stage == CallStage.FAILED && counterpartyPhone != null
}

/**
 * **SCR-DA-031 · the in-app call** (US-6A.16, P-05, D-24, AL-48).
 *
 * **P-05 — the driver calls the RIDER, never the booker.** On a proxy booking the person in the
 * vehicle is not the person who paid, and both halves of this screen already respect that:
 * `calleeRole = passenger` is what `comms.call_log` records, and `RideDetail.counterpartyPhone` is
 * the rider's own number, which ride-svc resolves — not this screen.
 *
 * **AL-48 — a failure falls back to a direct dial, not to a relay.** The masked-number PSTN bridge
 * and D-25's masked-SMS relay are both withdrawn, so there is exactly one fallback and it is a
 * `tel:` dial of the real number the ride carries post-accept. [dialDirectly] takes it, and it logs
 * a second `direct_dial` call against the same ride so the platform can tell a fallback from a
 * driver who simply preferred to dial.
 *
 * **The outcome is always reported, and it is best-effort.** `POST /v1/calls/{callId}/outcome` is
 * the only way voip-svc learns about a call that never connected; a failure to write it must never
 * be visible here, which is why every report is wrapped rather than guarded.
 *
 * @param engine The WebRTC seam. **This build binds [AbsentVoipEngine]** — read its KDoc before
 *   concluding that the connected state is unreachable by accident.
 */
internal class VoipCallViewModel(
    private val rideId: Ulid,
    private val rides: ActiveRideRepository,
    private val contact: RideContact,
    private val engine: VoipEngine,
) : ViewModel() {

    private val mutableState = MutableStateFlow(VoipCallState())

    val state: StateFlow<VoipCallState> = mutableState.asStateFlow()

    /** The `comms.call_log` row this screen opened, for the outcome report. */
    private var callId: Ulid? = null

    private var timer: Job? = null

    init {
        place()
    }

    /**
     * Reads the ride, starts the call and joins the room.
     *
     * The ride is read **first** and its failure is not fatal: the name and the fallback number are
     * what it supplies, and a call the driver can still place with a blank header is better than a
     * screen that refuses to try. What is fatal is `POST /v1/calls/start` answering without a
     * session — that is [VoipFailure.SIGNALLING], and there is nothing to join.
     */
    private fun place() {
        viewModelScope.launch {
            runCatching { rides.detail(rideId) }.getOrNull()?.let { ride ->
                mutableState.update {
                    it.copy(calleeName = ride.riderName, counterpartyPhone = ride.counterpartyPhone)
                }
            }

            val session = runCatching {
                contact.startCall(rideId, CalleeRole.PASSENGER, CallType.FREE_VOIP)
                    .also { callId = it.callId }
                    .session
            }.getOrNull()

            if (session == null) {
                fail(VoipFailure.SIGNALLING)
                return@launch
            }

            // Cold flow: this collection IS the room membership, so `onCleared` cancelling the
            // scope leaves the call. Nothing else has to remember to hang up.
            engine.join(session).collect(::onLink)
        }
    }

    private fun onLink(link: CallLink) {
        when (link) {
            CallLink.Connecting -> mutableState.update { it.copy(stage = CallStage.CONNECTING) }
            CallLink.Connected -> onConnected()
            is CallLink.Failed -> fail(link.reason)
        }
    }

    /** Enters the connected state once, and starts the wireframe's `00:42`. */
    private fun onConnected() {
        if (mutableState.value.stage == CallStage.CONNECTED) return
        mutableState.update { it.copy(stage = CallStage.CONNECTED, seconds = 0, failure = null) }

        timer?.cancel()
        timer = viewModelScope.launch {
            while (isActive) {
                delay(TICK)
                mutableState.update { it.copy(seconds = it.seconds + 1) }
            }
        }
    }

    /**
     * The failure state, and the `voip_failed` row that goes with it.
     *
     * Reported even when the driver never sees the screen again: a call that fell over is the
     * signal D6' §6 asks the client to produce, and it is what makes the direct-dial row that may
     * follow legible as a fallback.
     */
    private fun fail(reason: VoipFailure) {
        timer?.cancel()
        mutableState.update { it.copy(stage = CallStage.FAILED, failure = reason) }
        report(CallOutcome.VOIP_FAILED)
    }

    /** The 🔇 toggle. Local state and an engine call — nothing is sent to the platform. */
    fun toggleMute() {
        val muted = !mutableState.value.muted
        engine.setMicrophoneMuted(muted)
        mutableState.update { it.copy(muted = muted) }
    }

    /** The 🔊 toggle. */
    fun toggleSpeaker() {
        val on = !mutableState.value.speakerOn
        engine.setSpeakerphoneOn(on)
        mutableState.update { it.copy(speakerOn = on) }
    }

    /**
     * The red disc — hang up.
     *
     * A call that connected ends [CallOutcome.COMPLETED]; one abandoned while it was still ringing
     * is [CallOutcome.CANCELLED], which is the caller's own word for it. A call that had already
     * failed has reported its outcome and does not report a second.
     */
    fun hangUp() {
        val stage = mutableState.value.stage
        engine.leave()
        timer?.cancel()
        if (stage != CallStage.FAILED) {
            report(if (stage == CallStage.CONNECTED) CallOutcome.COMPLETED else CallOutcome.CANCELLED)
        }
        mutableState.update { it.copy(finished = true) }
    }

    /**
     * **AL-48's fallback** — *"Call normally instead?"* (US-26.4, BR-30.3).
     *
     * A **direct cellular dial** of the rider's real number, which `RideDetail.counterpartyPhone`
     * carries from `Accepted` onward. There is no masking bridge and no SMS relay to fall back to;
     * both were withdrawn. The `direct_dial` row is written before the dial and is best-effort,
     * because a `tel:` dial cannot be server-verified and a logging failure must not stop it.
     */
    fun dialDirectly() {
        val number = mutableState.value.counterpartyPhone ?: return
        engine.leave()
        timer?.cancel()

        viewModelScope.launch {
            runCatching { contact.startCall(rideId, CalleeRole.PASSENGER, CallType.DIRECT_DIAL) }
            mutableState.update { it.copy(dialNumber = number) }
        }
    }

    /** Clears the dial number once the screen has handed it to the platform dialler. */
    fun consumeDial() {
        mutableState.update { it.copy(dialNumber = null, finished = true) }
    }

    @Suppress("TooGenericExceptionCaught") // The log is best-effort; nothing here reaches the driver.
    private fun report(outcome: CallOutcome) {
        val id = callId ?: return
        viewModelScope.launch {
            try {
                contact.reportCallOutcome(id, outcome)
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // Deliberately swallowed. `comms.call_log.outcome` is documented best-effort, and
                // a driver mid-emergency must never be shown a failure about a log row.
            }
        }
    }

    private companion object {

        /** One second — the wireframe's timer resolution, and nothing finer is drawn. */
        val TICK: Duration = 1.seconds
    }
}
