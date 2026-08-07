import AVFoundation
import CallKit
import Foundation

/// SCR-DI-031's CallKit half — the system's idea that this app has a call up.
///
/// **D2' §SCR-DI-031: *"**iOS** CallKit"***, and the wireframe's own cell is titled *"VoIP call
/// (CallKit)"*. This is the platform counterpart of the `ConnectionService` D2' §C names for
/// Android, and it is what the app gets from it:
///
/// - **The audio session.** CallKit owns it. Without a reported call, the app cannot take the
///   `.voiceChat` route, cannot duck navigation audio and cannot hold the microphone in the
///   background — a VoIP call that is not reported is a call that dies when the screen locks.
/// - **The lock screen, the status-bar pill and the system's Recents list.** An outgoing CallKit
///   call does **not** take over the app's UI, which is why the wireframe's own screen is still what
///   the driver sees: the system chrome sits around it.
/// - **`CXCallObserver.calls`**, which is what ``SystemRideContact/dial(_:)`` already checks before
///   placing a `tel:` dial. That guard is C088's and it has been waiting for this class: on iOS a
///   `tel:` URL *places* a call, so dialling over a live one hangs it up. Reporting here is what
///   makes the refusal on the ride screen true rather than theoretical.
///
/// **The call is reported from the LINK, never from the tap.** ``VoipCallModel`` calls
/// ``startedConnecting()`` on ``CallLink/connecting`` and ``connected()`` on ``CallLink/connected``;
/// a failure calls ``end(reason:)``. So a build whose engine is ``AbsentVoipEngine`` — which fails
/// before it ever reports connecting — reports **no call at all**, and the driver does not see a
/// system call flash into the status bar and out again for a call that never happened. That is the
/// honest wiring, and it is also the one that needs no change when the real engine lands.
///
/// **`CXProvider` rather than `CXCallController`.** The controller is for *requesting* a call, which
/// asks the system to ask the app to start one — a round trip that exists so Siri and the Recents
/// list can place calls through the app. Nothing here is reached that way (SCR-DI-031 is opened from
/// a ride the driver is already on), so the provider's `reportOutgoingCall` pair is the whole of it.
/// The delegate is still implemented, because CallKit's own controls — the red button on the lock
/// screen and the mute switch in Control Centre — arrive through it and must reach the model.
///
/// A protocol for the reason every seam in this target is one: `CXProvider` needs a real
/// CallKit-capable process, and a model test has none.
@MainActor
protocol CallSession: AnyObject {

    /// Tells the system a call to [handle] is being placed. The `Connecting…` state.
    func startedConnecting(handle: String)

    /// Tells the system media is flowing. Starts the system's own call timer.
    func connected()

    /// Ends the reported call, if there is one. Idempotent.
    func end(reason: CallEndReason)

    /// Mirrors the app's 🔇 onto the system, so Control Centre agrees with the screen.
    func setMuted(_ muted: Bool)

    /// The system's own **End** — the lock-screen button and the Recents list. The model hangs up.
    var onSystemEnd: (() -> Void)? { get set }

    /// The system's own mute switch. The model updates the screen rather than toggling again.
    var onSystemMute: ((Bool) -> Void)? { get set }
}

/// Why a reported call ended, in CallKit's own vocabulary.
///
/// Distinct from `CallOutcome`, which is voip-svc's and is what the platform logs: this one decides
/// what the **handset's** call history says, and the two answer different questions. A call the
/// driver hung up is `.remoteEnded`-free — it simply ends — while one this build could not carry is
/// `.failed`, which is what puts it in Recents as a failed call rather than a completed one.
enum CallEndReason {

    /// The driver hung up, or the ride ended under them.
    case localEnded

    /// The call could not be carried — signalling, media, or no client at all.
    case failed
}

/// ``CallSession`` over `CXProvider`.
@MainActor
final class CallKitSession: NSObject, CallSession {

    var onSystemEnd: (() -> Void)?
    var onSystemMute: ((Bool) -> Void)?

    private let provider: CXProvider
    private var callId: UUID?

    override init() {
        provider = CXProvider(configuration: Self.configuration())
        super.init()
        provider.setDelegate(self, queue: nil)
    }

    func startedConnecting(handle: String) {
        guard callId == nil else { return }

        let identifier = UUID()
        callId = identifier

        // `CXHandle(type: .generic)` and **not** `.phoneNumber`. P-05's whole point is that the
        // driver never sees the rider's number on a free call, and a `.phoneNumber` handle is
        // rendered by the system on the lock screen and written into the handset's own call
        // history — which would leak, to the OS and to anybody who picked up the phone, precisely
        // the number AL-48 says a VoIP call keeps hidden.
        let update = CXCallUpdate()
        update.remoteHandle = CXHandle(type: .generic, value: handle)
        update.localizedCallerName = handle
        update.hasVideo = false
        update.supportsGrouping = false
        update.supportsUngrouping = false
        update.supportsHolding = false
        update.supportsDTMF = false

        provider.reportOutgoingCall(with: identifier, startedConnectingAt: nil)
        provider.reportCall(with: identifier, updated: update)
    }

    func connected() {
        guard let callId else { return }
        provider.reportOutgoingCall(with: callId, connectedAt: nil)
    }

    func end(reason: CallEndReason) {
        guard let callId else { return }
        self.callId = nil
        provider.reportCall(
            with: callId,
            endedAt: nil,
            reason: reason == .failed ? .failed : .remoteEnded
        )
    }

    /// **Deliberately a no-op, and the seam exists anyway.**
    ///
    /// CallKit has no *"the app muted itself"* report: mute travels the other way, as a
    /// `CXSetMutedCallAction` the system asks this provider to perform, and there is no
    /// `CXCallUpdate` field for it. The in-app 🔇 therefore moves the **engine** and the screen, and
    /// Control Centre's switch arrives through ``onSystemMute`` — see ``VoipCallModel``.
    ///
    /// The method is on the protocol rather than absent from it so the direction of travel is
    /// written down where somebody adding a real engine will read it, and so a future
    /// `CXCallController.request(CXTransaction(action: CXSetMutedCallAction(…)))` — the only way to
    /// push mute *to* the system — has one place to land.
    func setMuted(_ muted: Bool) {}

    /// The provider's configuration.
    ///
    /// `supportsVideo` is false and `maximumCallsPerCallGroup` is one: a driver has one rider and
    /// there is nothing to conference. `includesCallsInRecents` is left at its default `true`, so a
    /// call the driver made appears in their own history — which is the point of reporting it.
    private static func configuration() -> CXProviderConfiguration {
        let configuration = CXProviderConfiguration()
        configuration.supportsVideo = false
        configuration.maximumCallsPerCallGroup = 1
        configuration.maximumCallGroups = 1
        configuration.supportedHandleTypes = [.generic]
        return configuration
    }
}

extension CallKitSession: CXProviderDelegate {

    /// The system tore every call down — a reset, or the app being killed and relaunched.
    nonisolated func providerDidReset(_ provider: CXProvider) {
        Task { @MainActor [weak self] in
            self?.callId = nil
            self?.onSystemEnd?()
        }
    }

    /// The red button on the lock screen, or **End** in the Recents list.
    nonisolated func provider(_ provider: CXProvider, perform action: CXEndCallAction) {
        Task { @MainActor [weak self] in
            self?.callId = nil
            self?.onSystemEnd?()
            action.fulfill()
        }
    }

    /// Control Centre's mute switch.
    nonisolated func provider(_ provider: CXProvider, perform action: CXSetMutedCallAction) {
        Task { @MainActor [weak self] in
            self?.onSystemMute?(action.isMuted)
            action.fulfill()
        }
    }

    /// CallKit has activated the audio session and the engine may start capture.
    ///
    /// Nothing to do while ``AbsentVoipEngine`` is bound — there is no capture to start — but the
    /// callback is where a real engine's `startAudio()` goes, and it is the reason the audio session
    /// is CallKit's and not the app's.
    nonisolated func provider(_ provider: CXProvider, didActivate audioSession: AVAudioSession) {}

    nonisolated func provider(_ provider: CXProvider, didDeactivate audioSession: AVAudioSession) {}
}
