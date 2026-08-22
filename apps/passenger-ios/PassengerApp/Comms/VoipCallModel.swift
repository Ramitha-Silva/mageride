import Combine
import Foundation
import MageRideShared

/// Where SCR-PI-028 is. At most one at a time, which is why it is an enum rather than three flags.
enum CallStage {

    /// The token is being minted, or the room is being joined.
    case connecting

    /// Media is flowing. The timer runs from the moment this is entered.
    case connected

    /// The call could not be carried. AL-48's *"Call normally instead?"* is offered from here.
    case failed
}

/// SCR-PI-028's state.
///
/// - Parameters:
///   - stage: Which of the wireframe's three states is drawn.
///   - calleeName: Who is being called — the wireframe's *"K. Fernando"*. `nil` until the ride has
///     been read, which is why the screen falls back to *"Your driver"* rather than a blank line.
///   - counterpartyPhone: The driver's real number, which AL-48's fallback dials.
///   - failure: Why the call failed, which decides the copy under the failure.
///   - seconds: How long the call has been connected, for `Connected · 01:24`.
///   - isMuted: The 🔇 toggle.
///   - isSpeakerOn: The 🔊 toggle.
///   - errorKey: Copy for a fallback dial the handset refused.
///   - isFinished: The call is over and the takeover should close.
struct VoipCallState {

    var stage: CallStage = .connecting
    var calleeName: String?
    var counterpartyPhone: String?
    var failure: VoipFailure?
    var seconds: Int64 = 0
    var isMuted = false
    var isSpeakerOn = false
    var errorKey: String?
    var isFinished = false

    /// Whether *"Call normally instead?"* is offered (AL-48, US-26.4).
    ///
    /// A failed call that has a number to fall back to. `RideDetail.counterpartyPhone` is carried
    /// only from `Accepted` onward, and a ride that has ended answers `409 ride-terminal` and
    /// carries none — so this stays false there: there is nobody left to reach, and offering a dial
    /// would be wrong advice rather than a fallback.
    var canDialDirectly: Bool { stage == .failed && counterpartyPhone != nil }

    /// Whether the two toggles are live. They are gone once a call has failed — there is no audio
    /// to mute.
    var isLive: Bool { stage != .failed }
}

/// **SCR-PI-028 · the in-app call** (US-6A.16, D-24, AL-48).
///
/// **The one door to `POST /v1/calls/start` for a free call.** SCR-PI-015a records the passenger's
/// *choice* and navigates here; this screen places the call. If the chooser also started one, a
/// single tap would write two `comms.call_log` rows for one conversation.
///
/// **AL-48 — a failure falls back to a direct dial, not to a relay.** The masked-number PSTN bridge
/// and D-25's masked-SMS relay are both withdrawn, so there is exactly one fallback and it is a
/// `tel:` dial of the real number the ride carries post-accept. ``dialDirectly()`` takes it, and it
/// logs a second `direct_dial` call against the same ride so the platform can tell a fallback from a
/// passenger who simply preferred to dial.
///
/// **The outcome is always reported, and it is best-effort.** `POST /v1/calls/{callId}/outcome` is
/// the only way voip-svc learns about a call that never connected; a failure to write it must never
/// be visible here, which is why ``report(_:)`` swallows.
///
/// **CallKit is driven by the LINK, not by the tap** — see ``CallKitSession``. That ordering is also
/// what keeps ``dialDirectly()`` working: on this platform a `tel:` URL **places** a call, so
/// dialling over one the system still believes is up would hang the new one straight back up. The
/// reported call is therefore ended on the way into the failure state, *before* the fallback is
/// offered. Getting that order wrong is a *"Call normally"* button that silently does nothing.
///
/// - Parameter engine: The WebRTC seam. **This build binds ``AbsentVoipEngine``** — read its
///   documentation before concluding that ``CallStage/connected`` is unreachable by accident.
@MainActor
final class VoipCallModel: ObservableObject {

    @Published private(set) var state = VoipCallState()

    private let rideId: String
    private let rides: RideRepository
    private let contact: RideContact
    private let engine: VoipEngine
    private let session: CallSession

    /// The `comms.call_log` row this screen opened, for the outcome report.
    private var callId: String?

    /// Whether ``start()`` has already run. Separate from ``callId``, which is only assigned once
    /// `POST /v1/calls/start` answers: two synchronous `start()` calls both pass a `callId == nil`
    /// guard and both place a call, and SCR-PI-015a's rule is one tap, one `comms.call_log` row.
    private var didStart = false

    /// Whether the system was ever told a call was coming up. `end` is only honest after
    /// `startedConnecting`, and a build with no media client never gets that far.
    private var systemToldOfCall = false

    private var timer: Task<Void, Never>?

    init(
        rideId: String,
        rides: RideRepository,
        contact: RideContact,
        engine: VoipEngine,
        session: CallSession
    ) {
        self.rideId = rideId
        self.rides = rides
        self.contact = contact
        self.engine = engine
        self.session = session
    }

    deinit {
        timer?.cancel()
    }

    /// Reads the ride, starts the call and joins the room.
    ///
    /// The ride is read **first** and its failure is not fatal: the name and the fallback number are
    /// what it supplies, and a call the passenger can still place with a blank header is better than
    /// a screen that refuses to try. What is fatal is `POST /v1/calls/start` answering without a
    /// session — that is ``VoipFailure/signalling``, and there is nothing to join.
    ///
    /// Called from the screen's `.task` and guarded, because SwiftUI may run `.task` again after a
    /// scene change and a second run would write a second `comms.call_log` row for one tap.
    func start() {
        guard !didStart, state.stage == .connecting, state.failure == nil else { return }
        didStart = true

        session.onSystemEnd = { [weak self] in self?.hangUp() }
        session.onSystemMute = { [weak self] muted in self?.applySystemMute(muted) }

        // `guard let self` once, at the top, rather than `self?.` per line: this task has to survive
        // to seat the failure state even if the screen is going away, because the `voip_failed`
        // outcome is the only signal voip-svc gets about a call that never connected.
        Task { [weak self] in
            guard let self else { return }

            if let ride = try? await self.rides.ride(rideId: self.rideId) {
                self.state.calleeName = ride.driver?.name
                self.state.counterpartyPhone = ride.counterpartyPhone
            }

            let started = try? await self.rides.startCall(rideId: self.rideId, type: CallType.freeVoip)
            self.callId = started?.callId

            guard let voipSession = started?.session else {
                self.fail(.signalling)
                return
            }

            self.engine.join(session: voipSession) { [weak self] link in
                self?.onLink(link)
            }
        }
    }

    /// The 🔇 toggle. Local state, an engine call and the system's own switch — nothing is sent to
    /// the platform.
    func toggleMute() {
        let muted = !state.isMuted
        engine.setMicrophoneMuted(muted)
        session.setMuted(muted)
        state.isMuted = muted
    }

    /// The 🔊 toggle.
    ///
    /// Deliberately **not** reported to CallKit: the speaker is an audio-route choice the engine
    /// makes on the session CallKit owns, and there is no `CX…Action` for one. On a build with a
    /// real engine this is `AVAudioSession.overrideOutputAudioPort`; on this one it is a no-op that
    /// still moves the screen, which is the same shape the mute toggle has.
    func toggleSpeaker() {
        let on = !state.isSpeakerOn
        engine.setSpeakerphoneOn(on)
        state.isSpeakerOn = on
    }

    /// The red disc — hang up.
    ///
    /// A call that connected ends `CallOutcome.completed`; one abandoned while it was still ringing
    /// is `CallOutcome.cancelled`, which is the caller's own word for it. A call that had already
    /// failed has reported its outcome and does not report a second.
    func hangUp() {
        guard !state.isFinished else { return }

        let stage = state.stage
        engine.leave()
        timer?.cancel()
        timer = nil
        if systemToldOfCall { session.end(reason: .localEnded) }

        if stage != .failed {
            report(stage == .connected ? CallOutcome.completed : CallOutcome.cancelled)
        }
        state.isFinished = true
    }

    /// **AL-48's fallback** — *"Call normally instead?"* (US-26.4).
    ///
    /// A **direct cellular dial** of the driver's real number, which `RideDetail.counterpartyPhone`
    /// carries from `Accepted` onward. There is no masking bridge and no SMS relay to fall back to;
    /// both were withdrawn. The `direct_dial` row is written before the dial and is best-effort,
    /// because a `tel:` dial cannot be server-verified and a logging failure must not stop it.
    ///
    /// **Δ iOS — the dial is placed here, not handed to the view.** `ACTION_DIAL` on Android only
    /// *opens* the dialler, so `apps/passenger-android` passes a number back through its state for a
    /// `LaunchedEffect` to fire; a `tel:` URL on iOS **places** the call, which is why it goes
    /// through ``RideContact/dial(_:)`` and why a handset that claims no `tel:` URL at all (an iPad,
    /// a simulator) is a state this screen has to draw rather than a success it can assume. The
    /// reported call has already been ended by ``fail(_:)``, so the line is clear.
    func dialDirectly() {
        guard let phone = state.counterpartyPhone else { return }
        engine.leave()
        timer?.cancel()
        timer = nil

        Task { [weak self] in
            guard let self else { return }
            _ = try? await self.rides.startCall(rideId: self.rideId, type: CallType.directDial)
            guard await self.contact.dial(phone) else {
                self.state.errorKey = "call_dial_refused"
                return
            }
            self.state.isFinished = true
        }
    }

    /// Clears the last failure once its copy has been read.
    func consume() {
        state.errorKey = nil
    }

    // MARK: -

    private func onLink(_ link: CallLink) {
        switch link {
        case .connecting:
            state.stage = .connecting
            // The system learns about the call the moment the room does, and never before — so a
            // build with no media client reports nothing at all rather than flashing a call into
            // the status bar and out again.
            session.startedConnecting(handle: state.calleeName ?? "call_driver".localised)
            systemToldOfCall = true

        case .connected:
            onConnected()

        case .failed(let reason):
            fail(reason)
        }
    }

    /// Enters the connected state once, and starts the wireframe's `01:24`.
    private func onConnected() {
        guard state.stage != .connected else { return }
        state.stage = .connected
        state.seconds = 0
        state.failure = nil
        session.connected()

        timer?.cancel()
        timer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: Self.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.state.seconds += 1
            }
        }
    }

    /// The failure state, and the `voip_failed` row that goes with it.
    ///
    /// Reported even when the passenger never sees the screen again: a call that fell over is the
    /// signal D6' §6 asks the client to produce, and it is what makes the direct-dial row that may
    /// follow legible as a fallback.
    private func fail(_ reason: VoipFailure) {
        timer?.cancel()
        timer = nil
        state.stage = .failed
        state.failure = reason
        // Before the fallback is offered, so a `tel:` dial the passenger takes is not placed over a
        // call the system still believes is up — see ``dialDirectly()``. Only if the system was told
        // of one at all: a build with no media client fails before `.connecting`, and ending a call
        // CallKit never heard of is the status-bar flash `onLink` says must not happen.
        if systemToldOfCall { session.end(reason: .failed) }
        report(CallOutcome.voipFailed)
    }

    /// Control Centre's mute switch moved. The engine follows the system rather than the screen.
    private func applySystemMute(_ muted: Bool) {
        guard state.isMuted != muted else { return }
        engine.setMicrophoneMuted(muted)
        state.isMuted = muted
    }

    /// Best-effort, and it captures no `self`: the report has to outlive the screen. A passenger who
    /// hangs up and swipes away has still had a call, and voip-svc has to be told how it ended.
    private func report(_ outcome: CallOutcome) {
        guard let callId else { return }
        let rides = self.rides
        Task { try? await rides.reportCallOutcome(callId: callId, outcome: outcome) }
    }

    /// One second — the wireframe's timer resolution, and nothing finer is drawn.
    private static let tickNanoseconds: UInt64 = 1_000_000_000
}
