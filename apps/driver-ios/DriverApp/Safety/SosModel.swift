import Combine
import Foundation
import MageRideShared

/// Where SCR-DI-032 is. One at a time — the alarm is raised once and does not go back.
enum SosStage {

    /// The wireframe's big red disc, with the countdown running under it.
    case armed

    /// `POST /v1/sos` is in flight. D-33's five-second budget is measured across this.
    case sending

    /// safety-svc answered. The alert is recorded and is on the admin live feed.
    case dispatched

    /// The call did not reach safety-svc at all. Nothing was raised, and the screen says so.
    case failed
}

/// SCR-DI-032's state.
///
/// - Parameters:
///   - stage: Which of the four states the screen draws.
///   - secondsLeft: The auto-send countdown, while ``stage`` is ``SosStage/armed``.
///   - contact: Who the SMS goes to (AL-13). `nil` means nobody is on file.
///   - isContactLoaded: Whether the emergency-contact read has answered yet.
///   - position: The driver's own last fix — the coordinate the alert carries.
///   - smsStatus: What D-33's parallel gateways managed, once dispatched.
///   - errorKey: Resolved copy for a failure to reach safety-svc at all.
struct SosState {

    var stage: SosStage = .armed
    var secondsLeft: Int = SosModel.countdownSeconds
    var contact: EmergencyContact?
    var isContactLoaded = false
    var position: Fix?
    var smsStatus: SosSmsStatus?
    var errorKey: String?

    /// Whether the alarm has been raised. Sticky — nothing takes the screen back out of it.
    var isRaised: Bool { stage == .dispatched }

    /// Whether the AL-13 warning is drawn — *"no emergency contact, so the SMS has nobody to go to"*.
    ///
    /// Shown **before** the alarm rather than instead of it: `POST /v1/sos` still records the event
    /// and still raises the admin live feed with no contact on file, and refusing to send would take
    /// away the half that does work. safety-svc answers `SosSmsStatus.noContact` for the other half.
    var warnsNoContact: Bool { isContactLoaded && contact == nil }

    /// Whether the screen is still waiting for a fix to attach to the alarm.
    ///
    /// **`POST /v1/sos` has no positionless form**: `TriggerSosRequest.lat`/`.lng` are required, so
    /// there is no request to make until the handset has answered once. BR-29.4 contemplates exactly
    /// this case for the *web* surface — *"geolocation denied → SOS still fires with the last known
    /// driver-reported position"* — and the app-facing contract carries no equivalent. Recorded as a
    /// spec gap in the C075 handoff and carried forward here. In practice it is milliseconds:
    /// ``DriverLocationSource`` emits the **last known** fix before it registers for updates, and
    /// SCR-DI-015 already refuses to open this screen without one.
    var isAwaitingPosition: Bool { stage == .armed && position == nil }
}

/// **SCR-DI-032 · the driver SOS** (US-12.8, AL-13, D-33).
///
/// **Active trip only.** D2' §SCR-DI-032 scopes the driver's alarm to a trip in progress, which is
/// why ``DriverRoute/sos(rideId:)`` carries a ride and this class takes one: the SMS and the admin
/// live feed both say *which* trip, and `safety.sos_events.ride_id` is what an operator opens.
///
/// **The countdown is a cancel window, not a delay.** D5' §14.3 budgets **p99 ≤ 5 s** from the
/// request to the SMS leaving both gateways, so seconds spent before the request are seconds taken
/// off somebody's help. Three of them buys back the mis-tap — the disc is the largest control in the
/// app and it is pressed by somebody who is not looking — and ``raise()`` sends immediately when the
/// driver taps rather than waiting out the timer they interrupted.
///
/// **A failed SMS is not a failed SOS.** `SosSmsStatus.failed` means the alert **is** recorded and
/// **is** on the admin live feed and the SMS leg did not manage it; the screen says exactly that.
/// Only a request that never reached safety-svc is ``SosStage/failed``, and that one offers a retry.
///
/// An `ObservableObject` with one `@Published` struct, which is this target's shape for every model:
/// the Android twin's `StateFlow<SosState>` and this are the same object under two frameworks.
@MainActor
final class SosModel: ObservableObject {

    @Published private(set) var state = SosState()

    private let rideId: String
    private let contact: RideContact
    private let profiles: ProfileRepository
    private let location: DriverLocationSource

    private var countdown: Task<Void, Never>?

    init(rideId: String, contact: RideContact, profiles: ProfileRepository, location: DriverLocationSource) {
        self.rideId = rideId
        self.contact = contact
        self.profiles = profiles
        self.location = location
    }

    deinit {
        countdown?.cancel()
    }

    /// Reads the emergency contact and starts watching for a fix.
    ///
    /// Called from the screen's `.task` rather than from `init`, which is this target's rule: a
    /// `@StateObject` is constructed eagerly by SwiftUI and a model that started a GNSS subscription
    /// in its initialiser would hold one for a screen that was never shown.
    func start() {
        readContact()
        observePosition()
    }

    /// Stops the countdown and the fix subscription. The screen owns neither once it is gone.
    ///
    /// **Not a cancel of the alarm.** A request already in flight is not revocable and is
    /// deliberately not cancelled here: `POST /v1/sos` has left, safety-svc will record it, and a
    /// screen going away must not be able to un-raise an alarm.
    func stop() {
        countdown?.cancel()
        countdown = nil
        location.stop()
    }

    /// The wireframe's SOS disc — raise the alarm now.
    ///
    /// Idempotent from the driver's side: a second tap while the request is in flight or after it
    /// has been answered does nothing, because there is one alarm per trip and a second `POST` would
    /// be a second row on the operator's feed for the same emergency.
    ///
    /// The fix used is the **last known** one, never a fresh read: waiting for a GPS lock inside
    /// D-33's five-second budget is how an alarm arrives after the moment it was needed.
    func raise() {
        guard state.stage == .armed else { return }

        countdown?.cancel()
        countdown = nil

        guard let at = state.position?.point else {
            state.stage = .failed
            state.errorKey = "sos_no_position"
            return
        }

        state.stage = .sending
        state.errorKey = nil

        // `guard let self` once, at the top: an alarm in flight has to be able to seat its answer
        // even if the screen is going away, because `POST /v1/sos` is not revocable and the driver
        // has to be told whether it left.
        Task { [weak self] in
            guard let self else { return }
            do {
                let dispatched = try await self.contact.triggerSos(rideId: self.rideId, at: at)
                self.state.stage = .dispatched
                self.state.smsStatus = dispatched.smsStatus
            } catch {
                self.state.stage = .failed
                self.state.errorKey = OnboardingErrors.messageKey(for: error)
            }
        }
    }

    /// Puts the countdown back after a failure, so the disc is live again.
    func retry() {
        guard state.stage == .failed else { return }
        state.stage = .armed
        state.secondsLeft = Self.countdownSeconds
        state.errorKey = nil
        // Still nothing to send with, so nothing to count down to; the fix handler starts the
        // window when the handset answers.
        if state.position != nil { startCountdown() }
    }

    /// **Cancel** — stops the auto-send. Only reachable before the alarm has gone.
    func cancelCountdown() {
        countdown?.cancel()
        countdown = nil
    }

    /// `GET /v1/me/emergency-contacts` — the row the wireframe draws under the disc (AL-13).
    private func readContact() {
        Task { [weak self] in
            guard let self else { return }
            let all = (try? await self.profiles.emergencyContacts()) ?? []
            // "exactly one per account that has any" — the primary is what D-33's fast path
            // denormalises onto `iam.users`, so it is the one the SMS will actually reach.
            self.state.contact = all.first(where: \.isPrimary) ?? all.first
            self.state.isContactLoaded = true
        }
    }

    /// The driver's own position, and the countdown that waits for it.
    ///
    /// The window starts on the **first fix** rather than on appearance, because an alarm that fired
    /// by itself with no coordinate to carry would have nothing to send (see
    /// ``SosState/isAwaitingPosition``). The first emission is the last-known fix, so on any handset
    /// that has ever had one this is the same instant the screen appeared.
    private func observePosition() {
        location.start { [weak self] fix in
            guard let self else { return }
            let isFirst = self.state.position == nil
            self.state.position = fix
            if isFirst, self.state.stage == .armed { self.startCountdown() }
        }
    }

    /// The cancel window. Ticks down to zero and then raises the alarm by itself.
    private func startCountdown() {
        countdown?.cancel()
        countdown = Task { [weak self] in
            while !Task.isCancelled, (self?.state.secondsLeft ?? 0) > 0 {
                try? await Task.sleep(nanoseconds: Self.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.state.secondsLeft -= 1
            }
            guard !Task.isCancelled else { return }
            self?.raise()
        }
    }

    /// Three seconds of cancel window.
    ///
    /// **Not a spec number** — D5' §14.3 fixes the **dispatch** budget (p99 ≤ 5 s) and says nothing
    /// about a confirmation. Three is what is left of a five-second sense of urgency after a mis-tap
    /// has to be recoverable; anything longer starts spending the budget the SLO is about. The same
    /// number, for the same reason, as `SosViewModel.COUNTDOWN_SECONDS`. Recorded in the C075 handoff.
    static let countdownSeconds = 3

    private static let tickNanoseconds: UInt64 = 1_000_000_000
}
