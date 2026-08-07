import Combine
import Foundation
import MageRideShared

/// Where SCR-DI-031 is. At most one at a time, which is why it is an enum rather than three flags.
enum CallStage {

    /// The token is being minted, or the room is being joined.
    case connecting

    /// Media is flowing. The timer runs from the moment this is entered.
    case connected

    /// The call could not be carried. AL-48's *"Call normally instead?"* is offered from here.
    case failed
}

/// SCR-DI-031's state.
///
/// - Parameters:
///   - stage: Which of the wireframe's three states is drawn.
///   - calleeName: Who is being called — *"Nimal (rider)"*. `nil` until the ride is read.
///   - counterpartyPhone: The rider's real number (P-05), which AL-48's fallback dials.
///   - failure: Why the call failed, which decides the copy under the failure.
///   - seconds: How long the call has been connected, for `Connected · 00:42`.
///   - isMuted: The 🔇 toggle.
///   - isSpeakerOn: The 🔊 toggle.
///   - errorKey: Copy for a fallback dial the system refused.
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
    /// A failed call that has a number to fall back to. A ride that has ended answers
    /// `409 ride-terminal` and carries no `counterpartyPhone`, so this stays false: there is nobody
    /// left to reach, and offering a dial would be wrong advice rather than a fallback.
    var canDialDirectly: Bool { stage == .failed && counterpartyPhone != nil }

    /// Whether the two toggles are live. They are inert once a call has failed.
    var isLive: Bool { stage != .failed }
}

/// **SCR-DI-031 · the in-app call** (US-6A.16, P-05, D-24, AL-48).
///
/// **P-05 — the driver calls the RIDER, never the booker.** On a proxy booking the person in the
/// vehicle is not the person who paid, and both halves of this screen already respect that:
/// `CalleeRole.passenger` is what `comms.call_log` records, and `RideDetail.counterpartyPhone` is the
/// rider's own number, which ride-svc resolves — not this screen.
///
/// **AL-48 — a failure falls back to a direct dial, not to a relay.** The masked-number PSTN bridge
/// and D-25's masked-SMS relay are both withdrawn, so there is exactly one fallback and it is a
/// `tel:` dial of the real number the ride carries post-accept. ``dialDirectly()`` takes it, and it
/// logs a second `direct_dial` call against the same ride so the platform can tell a fallback from a
/// driver who simply preferred to dial.
///
/// **The outcome is always reported, and it is best-effort.** `POST /v1/calls/{callId}/outcome` is
/// the only way voip-svc learns about a call that never connected; a failure to write it must never
/// be visible here, which is why ``RideContact/reportCallOutcome(callId:outcome:)`` swallows.
///
/// **CallKit is driven by the LINK, not by the tap** — see ``CallKitSession``. That ordering is also
/// what keeps ``dialDirectly()`` working: `SystemRideContact.dial` refuses while `CXCallObserver`
/// sees a call, so the reported call is ended on the way into the failure state and the fallback
/// dial then has a clear line. Getting that order wrong is a *"Call normally"* button that silently
/// does nothing.
///
/// - Parameter engine: The WebRTC seam. **This build binds ``AbsentVoipEngine``** — read its
///   documentation before concluding that ``CallStage/connected`` is unreachable by accident.
@MainActor
final class VoipCallModel: ObservableObject {

    @Published private(set) var state = VoipCallState()

    private let rideId: String
    private let rides: ActiveRideRepository
    private let contact: RideContact
    private let engine: VoipEngine
    private let session: CallSession

    /// The `comms.call_log` row this screen opened, for the outcome report.
    private var callId: String?

    private var timer: Task<Void, Never>?

    init(
        rideId: String,
        rides: ActiveRideRepository,
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
    /// what it supplies, and a call the driver can still place with a blank header is better than a
    /// screen that refuses to try. What is fatal is `POST /v1/calls/start` answering without a
    /// session — that is ``VoipFailure/signalling``, and there is nothing to join.
    ///
    /// Called from the screen's `.task` and guarded, because SwiftUI may run `.task` again after a
    /// scene change and a second run would write a second `comms.call_log` row for one tap.
    func start() {
        guard callId == nil, state.stage == .connecting, state.failure == nil else { return }

        session.onSystemEnd = { [weak self] in self?.hangUp() }
        session.onSystemMute = { [weak self] muted in self?.applySystemMute(muted) }

        // `guard let self` once, at the top, rather than `self?.` per line: this task has to survive
        // to seat the failure state even if the screen is going away, because the `voip_failed`
        // outcome is the only signal voip-svc gets about a call that never connected.
        Task { [weak self] in
            guard let self else { return }

            if let ride = try? await self.rides.detail(rideId: self.rideId) {
                self.state.calleeName = ride.riderName
                self.state.counterpartyPhone = ride.counterpartyPhone
            }

            let started = await self.contact.startCall(
                rideId: self.rideId,
                calleeRole: CalleeRole.passenger,
                type: CallType.freeVoip
            )
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
        session.end(reason: .localEnded)

        if stage != .failed {
            report(stage == .connected ? CallOutcome.completed : CallOutcome.cancelled)
        }
        state.isFinished = true
    }

    /// **AL-48's fallback** — *"Call normally instead?"* (US-26.4, BR-30.3).
    ///
    /// A **direct cellular dial** of the rider's real number, which `RideDetail.counterpartyPhone`
    /// carries from `Accepted` onward. There is no masking bridge and no SMS relay to fall back to;
    /// both were withdrawn. The `direct_dial` row is written before the dial and is best-effort,
    /// because a `tel:` dial cannot be server-verified and a logging failure must not stop it.
    ///
    /// **Δ iOS — the dial is placed here, not handed to the view.** `ACTION_DIAL` on Android only
    /// *opens* the dialler, so the Android twin passes a number back through its state for a
    /// `LaunchedEffect` to fire; a `tel:` URL on iOS **places** the call, which is why it goes
    /// through ``RideContact/dial(_:)``'s `CXCallObserver` guard instead. The reported call has
    /// already been ended by ``fail(_:)``, so that guard sees a clear line.
    func dialDirectly() {
        guard let phone = state.counterpartyPhone else { return }
        engine.leave()
        timer?.cancel()
        timer = nil

        Task { [weak self] in
            guard let self else { return }
            await self.contact.startCall(
                rideId: self.rideId,
                calleeRole: CalleeRole.passenger,
                type: CallType.directDial
            )
            guard self.contact.dial(phone) else {
                self.state.errorKey = "ride_call_unavailable"
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
            session.startedConnecting(handle: state.calleeName ?? "call_rider".localised)

        case .connected:
            onConnected()

        case .failed(let reason):
            fail(reason)
        }
    }

    /// Enters the connected state once, and starts the wireframe's `00:42`.
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
    /// Reported even when the driver never sees the screen again: a call that fell over is the
    /// signal D6' §6 asks the client to produce, and it is what makes the direct-dial row that may
    /// follow legible as a fallback.
    private func fail(_ reason: VoipFailure) {
        timer?.cancel()
        timer = nil
        state.stage = .failed
        state.failure = reason
        // Before the fallback is offered, so `CXCallObserver` is clear when the driver takes it.
        session.end(reason: .failed)
        report(CallOutcome.voipFailed)
    }

    /// Control Centre's mute switch moved. The engine follows the system rather than the screen.
    private func applySystemMute(_ muted: Bool) {
        guard state.isMuted != muted else { return }
        engine.setMicrophoneMuted(muted)
        state.isMuted = muted
    }

    /// Best-effort, and it captures no `self`: the report has to outlive the screen. A driver who
    /// hangs up and swipes away has still had a call, and voip-svc has to be told how it ended.
    private func report(_ outcome: CallOutcome) {
        guard let callId else { return }
        let contact = self.contact
        Task { await contact.reportCallOutcome(callId: callId, outcome: outcome) }
    }

    /// One second — the wireframe's timer resolution, and nothing finer is drawn.
    private static let tickNanoseconds: UInt64 = 1_000_000_000
}
