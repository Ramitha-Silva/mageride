package lk.mageride.passenger.comms

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flowOf
import lk.mageride.shared.data.models.comms.VoipSession

/**
 * Where a VoIP call is, as the media client sees it.
 *
 * Three states and no more, because SCR-PA-028 draws three: *"Connecting…"*, *"Connected · 01:24"*
 * and the failure that raises AL-48's *"Call normally instead?"*. Hanging up is not a state the
 * engine reports — it is the passenger leaving, which the view model knows before the engine does.
 */
internal sealed interface CallLink {

    /** The room is being joined. The screen shows the callee and a connecting caption. */
    data object Connecting : CallLink

    /** Media is flowing both ways. The screen starts its timer from the first of these. */
    data object Connected : CallLink

    /** The call cannot be carried. [reason] is what the outcome log records. */
    data class Failed(val reason: VoipFailure) : CallLink
}

/**
 * Why a VoIP call failed.
 *
 * All three become `CallOutcome.VOIP_FAILED` on `POST /v1/calls/{callId}/outcome` — voip-svc's
 * column has one value for *"the free call did not happen"*, and a `direct_dial` row after one is
 * the fallback being taken (see `VoipApi.recordCallOutcome`). The distinction is for the
 * passenger-facing copy, which differs: a ride that has ended has nobody left to call, and telling
 * the passenger to dial normally would be wrong advice rather than a fallback.
 */
internal enum class VoipFailure {

    /** `POST /v1/calls/start` answered without a session, or answered an error. */
    SIGNALLING,

    /** The room was reached and the media path was not. */
    MEDIA,

    /**
     * **This build carries no WebRTC client at all** — see [AbsentVoipEngine].
     *
     * A distinct value rather than [MEDIA] because it is not a network condition: it is true on
     * every handset, every time, and it is what makes the direct-dial prompt the *only* outcome
     * SCR-PA-028 currently reaches.
     */
    NO_MEDIA_CLIENT,
}

/**
 * The WebRTC half of SCR-PA-028 — D6' §6's LiveKit room, behind an interface.
 *
 * A seam rather than a direct SDK call for the reason `LiveHubTransport` and `PassengerLocationSource`
 * are: everything above it — the call's state machine, its timer, its outcome reporting and AL-48's
 * fallback — is then ordinary Kotlin that runs on this build host, and the one part that needs a
 * radio and a media engine is the one part that is swapped.
 *
 * [join] is a cold [Flow]: collecting it joins the room and cancelling the collection leaves it, so
 * a view model that is cleared cannot leave a call running. [leave] is the deliberate hang-up.
 */
internal interface VoipEngine {

    /**
     * Joins [session]'s LiveKit room and reports the link until the collector goes away.
     *
     * @param session `StartCallResponse.session` — the room name, the JWT and the signalling URL,
     *   all three scoped to the ride and expiring at trip end.
     */
    fun join(session: VoipSession): Flow<CallLink>

    /** The wireframe's 🔇. */
    fun setMicrophoneMuted(muted: Boolean)

    /** The wireframe's 🔊. */
    fun setSpeakerphoneOn(on: Boolean)

    /** The wireframe's red disc. Idempotent — leaving a call that has already ended is a no-op. */
    fun leave()
}

/**
 * The engine this build ships, and it carries no media.
 *
 * ### Why (Δ C075, unchanged for C084 — a dependency wall rather than a decision)
 *
 * D6' §6 names **LiveKit** as the SFU and D2' §SCR-PA-028's component note says *"Android: WebRTC +
 * `ConnectionService`"*, so the intended client is `io.livekit:livekit-android`. That artifact
 * cannot be added here: `livekit-android` depends on `com.github.davidliu:audioswitch`, which is
 * published **only on JitPack**, and this repository resolves from `google()` (content-filtered to
 * three group prefixes) and `mavenCentral()` alone, with `RepositoriesMode.FAIL_ON_PROJECT_REPOS`.
 * Adding a build-from-source GitHub proxy to the resolution set for every module in the repo is a
 * supply-chain change to `settings.gradle.kts`, which is C001's file and not this component's to
 * widen. The driver app hit the identical wall on SCR-DA-031; both handoffs record it.
 *
 * ### What that means on the screen, and why it is still the specified behaviour
 *
 * The **signalling half is real and complete**: `POST /v1/calls/start` is called with `free_voip`,
 * voip-svc mints the room token and writes `comms.call_log`, and this engine then reports
 * [VoipFailure.NO_MEDIA_CLIENT] — which is exactly the condition AL-48 legislates for. SCR-PA-028
 * puts up *"Call normally instead?"* and the passenger reaches the driver on a `tel:` dial of the
 * number the ride already carries. Nothing is faked and nothing is silently swallowed: the outcome
 * is reported to voip-svc as `voip_failed`, so the platform's own call log tells the truth about
 * what this build can do.
 *
 * ### What landing the real engine takes
 *
 * Replace this binding in `passengerAppModule` and nothing else: add `RECORD_AUDIO` and
 * `MODIFY_AUDIO_SETTINGS` to the manifest (they are absent on purpose — `ManifestTest` asserts the
 * absence of permissions with no code behind them), and emit [CallLink.Connected] from the room's
 * connection callback. The view model, the screen, the timer and the fallback are already written
 * against this interface and do not change.
 */
internal class AbsentVoipEngine : VoipEngine {

    override fun join(session: VoipSession): Flow<CallLink> = flowOf(CallLink.Failed(VoipFailure.NO_MEDIA_CLIENT))

    override fun setMicrophoneMuted(muted: Boolean) = Unit

    override fun setSpeakerphoneOn(on: Boolean) = Unit

    override fun leave() = Unit
}
