import Combine
import Foundation
import MageRideShared

/// Where SCR-PI-029 is. One at a time — the alarm is raised once and does not go back.
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

/// SCR-PI-029's state.
///
/// - Parameters:
///   - stage: Which of the four states the screen draws.
///   - secondsLeft: The auto-send countdown, while ``stage`` is ``SosStage/armed``.
///   - contacts: Who is on file (AL-13, US-12.1). Empty means nobody.
///   - isContactsLoaded: Whether the emergency-contact read has answered yet.
///   - position: The passenger's own last fix — the coordinate the alert carries.
///   - smsStatus: What D-33's parallel gateways managed, once dispatched.
///   - shareLink: D-34's live trip link, minted after the alarm has gone.
///   - errorKey: Resolved copy for a failure to reach safety-svc at all.
struct SosState {

    var stage: SosStage = .armed
    var secondsLeft: Int = SosModel.countdownSeconds
    var contacts: [EmergencyContact] = []
    var isContactsLoaded = false
    var position: GeoPoint?
    var smsStatus: SosSmsStatus?
    var shareLink: String?
    var errorKey: String?

    /// Whether the alarm has been raised. Sticky — nothing takes the screen back out of it.
    var isRaised: Bool { stage == .dispatched }

    /// The contact D-33's fast path will actually reach.
    ///
    /// iam-svc promotes exactly one onto `iam.users.emergency_contact_name/phone` because the SLO is
    /// p99 ≤ 5 s and a join is not in it — and the app never sets `isPrimary` itself (see
    /// ``SosContacts``). The rest of the list is drawn so the passenger can see who is on file; only
    /// this one wears the `Sent` pill, because only this one is sent to.
    var primaryContact: EmergencyContact? {
        contacts.first(where: \.isPrimary) ?? contacts.first
    }

    /// Whether the AL-13 warning is drawn — *"nobody is on file, so the SMS has nowhere to go"*.
    ///
    /// Shown **before** the alarm rather than instead of it. Unlike the driver's SCR-DI-032 this is
    /// not merely informational: with `Safety:RequireEmergencyContact` at its default,
    /// `POST /v1/sos` answers `400 no-emergency-contact` and **nothing at all is raised** — so a
    /// passenger with an empty list is told here, while there is still time to add one on
    /// SCR-PI-027b, and the disc still tries because the setting can be off and because refusing
    /// locally would be this app deciding an outcome the platform owns.
    var warnsNoContact: Bool { isContactsLoaded && contacts.isEmpty }

    /// Whether the screen is still waiting for a fix to attach to the alarm.
    ///
    /// **`POST /v1/sos` has no positionless form**: `TriggerSosRequest.lat`/`.lng` are required, so
    /// there is no request to make until the handset has answered once. BR-29.4 contemplates exactly
    /// this case for the *web* surface — *"geolocation denied → SOS still fires with the last known
    /// driver-reported position"* — and the app-facing contract carries no equivalent. Recorded as a
    /// spec gap by C075, C084 and C093, and carried forward here.
    ///
    /// In practice it is milliseconds: ``PassengerLocationSource`` emits the **last known** fix
    /// before it registers for updates, and ``LastKnownFix`` usually has one before this screen is
    /// even reached.
    var isAwaitingPosition: Bool { stage == .armed && position == nil }
}

/// **SCR-PI-029 · the passenger SOS** (US-12.1, AL-13, D-33, D-34).
///
/// **Trip-scoped, because the wireframe's only door is SCR-PI-015's `⛨ SOS`.** The screen's own copy
/// is *"Sending GPS + trip to emergency contacts"* and `safety.sos_events.ride_id` is what an
/// operator opens, so the route carries a ride id and this class takes one. `POST /v1/sos` marks
/// `rideId` optional and would permit a trip-less alarm; no wireframe cell draws an entry point for
/// one.
///
/// **The countdown is a cancel window, not a delay.** D5' §14.3 budgets **p99 ≤ 5 s** from the
/// request to the SMS leaving both gateways, so seconds spent before the request are seconds taken
/// off somebody's help. Three of them buys back the mis-tap — the disc is the largest control on the
/// screen and it is pressed by somebody who is not looking — and ``raise()`` sends immediately when
/// the passenger taps rather than waiting out the timer they interrupted.
///
/// **A failed SMS is not a failed SOS.** `SosSmsStatus.failed` means the alert **is** recorded and
/// **is** on the admin live feed and the SMS leg did not manage it; the screen says exactly that.
/// Only a request that never reached safety-svc is ``SosStage/failed``, and that one offers a retry.
///
/// **The share link is minted after the alarm, never before it.** D-34's `POST /v1/trip-share/{id}`
/// is a second round trip, and putting it in front of `POST /v1/sos` would spend the five-second
/// budget on a link. It is also allowed to fail: an alarm that went out with no link to hand on is
/// still an alarm that went out.
///
/// **The position comes from the fix flow, not from ``LastKnownFix``.** This is the one screen in
/// the app that genuinely wants a *live* subscription rather than the recorded value C097 added:
/// the countdown starts on the first emission, and a passenger who has just opened the app in a
/// moving vehicle should send where they are now rather than where the last screen saw them. The
/// first emission of that flow **is** the last known fix, so on any handset that has ever had one
/// the two are the same value at the same instant.
@MainActor
final class SosModel: ObservableObject {

    @Published private(set) var state = SosState()

    private let rideId: String
    private let safety: SafetyRepository
    private let contacts: SosContacts
    private let locations: PassengerLocationSource

    private var countdown: Task<Void, Never>?
    private var fixes: AnyCancellable?

    init(
        rideId: String,
        safety: SafetyRepository,
        contacts: SosContacts,
        locations: PassengerLocationSource
    ) {
        self.rideId = rideId
        self.safety = safety
        self.contacts = contacts
        self.locations = locations
    }

    deinit {
        countdown?.cancel()
    }

    /// Reads the emergency contacts and starts watching for a fix.
    ///
    /// Called from the screen's `.task` rather than from `init`, which is this target's rule: a
    /// `@StateObject` is constructed eagerly by SwiftUI and a model that subscribed to
    /// ``PassengerLocationSource`` in its initialiser would light the blue status-bar indicator for
    /// a screen that was never shown.
    func start() {
        readContacts()
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
        fixes?.cancel()
        fixes = nil
    }

    /// The wireframe's SOS disc — raise the alarm now.
    ///
    /// Idempotent from the passenger's side: a second tap while the request is in flight or after it
    /// has been answered does nothing, because there is one alarm per trip and a second `POST` would
    /// be a second row on the operator's feed for the same emergency.
    ///
    /// The fix used is the **last known** one, never a fresh read: waiting for a GPS lock inside
    /// D-33's five-second budget is how an alarm arrives after the moment it was needed.
    func raise() {
        guard state.stage == .armed else { return }

        countdown?.cancel()
        countdown = nil

        guard let at = state.position else {
            state.stage = .failed
            state.errorKey = "sos_no_position"
            return
        }

        state.stage = .sending
        state.errorKey = nil

        // `guard let self` once, at the top: an alarm in flight has to be able to seat its answer
        // even if the screen is going away, because `POST /v1/sos` is not revocable and the
        // passenger has to be told whether it left.
        Task { [weak self] in
            guard let self else { return }
            do {
                let dispatched = try await self.safety.triggerSos(rideId: self.rideId, lat: at.lat, lng: at.lng)
                self.state.stage = .dispatched
                self.state.smsStatus = dispatched.smsStatus
                await self.mintShareLink()
            } catch {
                self.state.stage = .failed
                self.state.errorKey = SafetyErrors.messageKey(for: error)
            }
        }
    }

    /// Puts the countdown back after a failure, so the disc is live again.
    func retry() {
        guard state.stage == .failed else { return }
        state.stage = .armed
        state.secondsLeft = Self.countdownSeconds
        state.errorKey = nil
        // Still nothing to send with, so nothing to count down to; the fix handler starts the window
        // when the handset answers.
        if state.position != nil { startCountdown() }
    }

    /// **Cancel** — stops the auto-send. Only reachable before the alarm has gone.
    func cancelCountdown() {
        countdown?.cancel()
        countdown = nil
    }

    // MARK: -

    /// `GET /v1/me/emergency-contacts` — the rows the wireframe draws under the disc (AL-13).
    private func readContacts() {
        Task { [weak self] in
            guard let self else { return }
            self.state.contacts = (try? await self.contacts.list()) ?? []
            self.state.isContactsLoaded = true
        }
    }

    /// The passenger's own position, and the countdown that waits for it.
    ///
    /// The window starts on the **first fix** rather than on appearance, because an alarm that fired
    /// by itself with no coordinate to carry would have nothing to send (see
    /// ``SosState/isAwaitingPosition``).
    private func observePosition() {
        guard fixes == nil else { return }
        fixes = locations.fixes.sink { [weak self] fix in
            guard let self else { return }
            let isFirst = self.state.position == nil
            self.state.position = fix.point
            if isFirst, self.state.stage == .armed { self.startCountdown() }
        }
    }

    /// D-34's live trip link, once the alarm is out.
    ///
    /// Best-effort by construction: it is minted **after** the state is already `dispatched`, and a
    /// failure leaves the alarm exactly where it is. `409 ride-terminal` is the ordinary case for a
    /// trip that ended while the screen was open — there is nothing left to follow, and saying so
    /// would be noise on top of an alarm that did go out.
    private func mintShareLink() async {
        guard let link = try? await safety.shareTrip(rideId: rideId) else { return }
        state.shareLink = link.url
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
    /// number, for the same reason, as `SosViewModel.COUNTDOWN_SECONDS` on the Android twin and
    /// `apps/driver-ios`'s `SosModel.countdownSeconds` — one platform should not have two answers to
    /// *"how long do I have to cancel"*. Recorded in the C075 handoff.
    static let countdownSeconds = 3

    private static let tickNanoseconds: UInt64 = 1_000_000_000
}
