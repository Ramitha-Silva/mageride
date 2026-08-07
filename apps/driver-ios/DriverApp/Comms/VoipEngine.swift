import Foundation
import MageRideShared

/// Where a VoIP call is, as the media client sees it.
///
/// Three cases and no more, because SCR-DI-031 draws three: *"Connecting…"*, *"Connected · 00:42"*
/// and the failure that raises AL-48's *"Call normally instead?"*. Hanging up is not a state the
/// engine reports — it is the driver leaving, which the model knows before the engine does.
enum CallLink: Equatable {

    /// The room is being joined. The screen shows the callee and a connecting caption.
    case connecting

    /// Media is flowing both ways. The screen starts its timer from the first of these.
    case connected

    /// The call cannot be carried.
    case failed(VoipFailure)
}

/// Why a VoIP call failed.
///
/// All three become `CallOutcome.voipFailed` on `POST /v1/calls/{callId}/outcome` — voip-svc's
/// column has one value for *"the free call did not happen"*, and a `direct_dial` row after one on
/// the same ride is the fallback being taken. The distinction is for the driver-facing copy, which
/// differs: a ride that has ended has nobody left to call, and telling the driver to dial normally
/// would be wrong advice.
enum VoipFailure: Equatable {

    /// `POST /v1/calls/start` answered without a session, or answered an error.
    case signalling

    /// The room was reached and the media path was not.
    case media

    /// **This build carries no WebRTC client at all** — see ``AbsentVoipEngine``.
    ///
    /// A distinct case rather than ``media`` because it is not a network condition: it is true on
    /// every handset, every time, and it is what makes the direct-dial prompt the *only* outcome
    /// SCR-DI-031 currently reaches.
    case noMediaClient
}

/// The WebRTC half of SCR-DI-031 — D6' §6's LiveKit room, behind a protocol.
///
/// A seam for the reason ``PositionPublisher`` and ``PaymentHandoff`` are: everything above it — the
/// call's state machine, its timer, its CallKit reporting, its outcome log and AL-48's fallback — is
/// then ordinary Swift, and the one part that needs a radio and a media engine is the one part that
/// is swapped.
///
/// ``join(session:onLink:)`` is a **subscription**: calling it joins the room and ``leave()`` leaves
/// it. An `AsyncStream` would have been the closer analogue of the Android twin's cold `Flow`, but a
/// media engine's connection callbacks arrive on its own queue and a model that owned a consuming
/// `Task` would have to cancel it on every path out of the screen; a handler plus an explicit
/// ``leave()`` is the shape ``DriverLocationSource`` already uses here for the same reason.
@MainActor
protocol VoipEngine: AnyObject {

    /// Joins [session]'s LiveKit room and reports the link to [onLink] until ``leave()``.
    ///
    /// - Parameter session: `StartCallResponse.session` — the room name, the JWT and the signalling
    ///   URL, all three scoped to the ride and expiring at trip end.
    func join(session: VoipSession, onLink: @escaping (CallLink) -> Void)

    /// The wireframe's 🔇.
    func setMicrophoneMuted(_ muted: Bool)

    /// The wireframe's 🔊.
    func setSpeakerphoneOn(_ on: Bool)

    /// The wireframe's red disc. Idempotent — leaving a call that has already ended is a no-op.
    func leave()
}

/// The engine this build ships, and it carries no media.
///
/// ### Why (Δ C093, and it is the same wall C075 hit from the other side)
///
/// D6' §6 names **LiveKit** as the SFU and D2' §SCR-DI-031 says *"**iOS** CallKit"*, so the intended
/// client is `livekit/client-sdk-swift`. Landing it here is not one file:
///
/// 1. It is a **remote** Swift package, and this project's only package today is
///    `shared/swiftpm/MageRideShared`, resolved **by path** (see `apps/driver-ios/CLAUDE.md`'s
///    Verify note). Adding a remote dependency changes what `xcodebuild` has to fetch before it can
///    build at all, which is a supply-chain decision about the app target rather than a screen's.
/// 2. It needs `NSMicrophoneUsageDescription` in `Info.plist`, which is deliberately **absent** —
///    that file's own header keeps a purpose string out until there is code behind it, exactly as
///    `apps/driver-android`'s manifest keeps `RECORD_AUDIO` out.
/// 3. Neither can be verified on this host: **iOS does not compile on Linux**, so a WebRTC
///    integration written here would be unverified code standing where a documented gap belongs.
///
/// The Android twin reached the same place for a different reason (`audioswitch` is JitPack-only and
/// this repo resolves from `mavenCentral()` alone), and this component is parity-fenced to it.
///
/// ### What that means on the screen, and why it is still the specified behaviour
///
/// The **signalling half is real and complete**: `POST /v1/calls/start` is called with `free_voip`,
/// voip-svc mints the room token and writes `comms.call_log`, and this engine then reports
/// ``VoipFailure/noMediaClient`` — which is exactly the condition AL-48 legislates for. SCR-DI-031
/// puts up *"Call normally instead?"* and the driver reaches the rider on a `tel:` dial of the
/// number the ride already carries. Nothing is faked and nothing is swallowed: the outcome is
/// reported to voip-svc as `voip_failed`, so the platform's own call log tells the truth about what
/// this build can do.
///
/// ### What landing the real engine takes
///
/// Replace this binding in ``DriverGraph`` and nothing else: add the package, add the purpose
/// string, report ``CallLink/connecting`` from `room.connect()` and ``CallLink/connected`` from the
/// connection callback. ``VoipCallModel``, ``VoipCallScreen``, ``CallKitSession`` and the fallback
/// are already written against this protocol and do not change — and ``CallKitSession`` is driven by
/// the **link**, so the system call appears the moment media does and never before.
@MainActor
final class AbsentVoipEngine: VoipEngine {

    func join(session: VoipSession, onLink: @escaping (CallLink) -> Void) {
        onLink(.failed(.noMediaClient))
    }

    func setMicrophoneMuted(_ muted: Bool) {}

    func setSpeakerphoneOn(_ on: Bool) {}

    func leave() {}
}
