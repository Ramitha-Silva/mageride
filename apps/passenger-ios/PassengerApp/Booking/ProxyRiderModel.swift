import Combine
import Foundation
import MageRideShared

/// SCR-PI-010b's state.
struct ProxyRiderState {

    var riderName = ""
    var riderPhone = ""
    var method: PickupMethod = .search

    /// `nil` until the number has been looked up. `false` is P-03's *"Not a MageRide user — enter
    /// pickup manually"* (US-8.19) — a real answer, not an error.
    var riderRegistered: Bool?
    var isLooking = false

    var requestId: String?

    /// Where the FCM round trip is (P-02). `nil` before one is sent.
    var requestState: LocationRequestState?

    /// The wireframe's `5:00`, from `CreateLocationRequestResponse.ttl`.
    var secondsLeft = 0

    /// What the chosen method produced.
    var pickup: Place?

    var errorKey: String?

    /// Whether the number is a `+947XXXXXXXX` at all — the local gate before any call goes out.
    var isPhoneComplete: Bool { PhoneNumber.isValid(riderPhone) }

    /// Whether *"Request rider location"* can fire.
    ///
    /// Blocked while one is already pending — P-12 allows five an hour, and a second tap while the
    /// first is still counting down would spend one of them on the same rider.
    var canRequestLocation: Bool {
        isPhoneComplete && riderRegistered != false && requestState != LocationRequestState.pending
    }

    /// Whether this screen is done and SCR-PI-009 can book.
    var isComplete: Bool { !riderName.trimmed.isEmpty && isPhoneComplete && pickup != nil }

    /// Whether the *"Waiting for rider… 5:00"* card is on screen.
    var isWaiting: Bool { requestState == LocationRequestState.pending }

    /// The wireframe's `5:00`.
    var countdown: String {
        String(format: "%d:%02d", secondsLeft / 60, secondsLeft % 60)
    }
}

/// SCR-PI-010b — who the ride is actually for.
///
/// **The location request is a round trip through two other systems and back** (P-02, P-13): this
/// app `POST`s it, ride-svc sends the rider a **silent FCM data message**, the rider answers on
/// SCR-PI-011, and the answer arrives here over **SignalR** in the
/// `booker:{bookerId}:loc-req:{requestId}` group. The socket is the primary channel and the poll is
/// the fallback — see ``awaitResolution(_:)`` — because a booker whose app was backgrounded through
/// the whole five minutes has no socket event to have missed.
///
/// **P-03 is checked before anything is sent.** `GET /v1/users/lookup` says whether the number
/// belongs to a registered user; if it does not there is nobody to FCM, and the screen says *"Not a
/// MageRide user — enter pickup manually"* (US-8.19) rather than sending a request that expires in
/// silence.
@MainActor
final class ProxyRiderModel: ObservableObject {

    @Published private(set) var state = ProxyRiderState()

    private let draft: BookingDraft
    private let bookings: BookingRepository
    private let live: PassengerLiveMap

    private var subscriptions: Set<AnyCancellable> = []
    private var work: [Task<Void, Never>] = []

    init(draft: BookingDraft, bookings: BookingRepository, live: PassengerLiveMap) {
        self.draft = draft
        self.bookings = bookings
        self.live = live
        state.riderName = draft.state.riderName
        state.riderPhone = draft.state.riderPhone
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Subscribes to the directed payload. Idempotent.
    func start() {
        guard subscriptions.isEmpty else { return }
        live.events
            .sink { [weak self] event in
                guard case .locationRequest(let payload) = event else { return }
                // The Confirmed carries the pin; a Declined carries nothing at all, which is P-02's
                // privacy rule visible in the payload rather than only in the copy.
                if let geo = payload.geo {
                    self?.setPickup(Place(lat: geo.lat, lng: geo.lng, address: nil))
                }
                self?.onResolved(requestId: payload.requestId, state: payload.state)
            }
            .store(in: &subscriptions)
    }

    /// Re-reads the pickup from the draft.
    ///
    /// The Search method leaves this screen for SCR-PI-008 and the answer lands on the draft (see
    /// ``CaptureTarget/proxyPickup``), so coming back is when it has to be picked up — a pushed
    /// SwiftUI view is not rebuilt from its initialiser when the stack pops back to it.
    func setPickupFromDraft() {
        guard let pickup = draft.state.pickup else { return }
        state.pickup = pickup
    }

    func onNameChanged(_ value: String) {
        state.riderName = value
        draft.update { $0.riderName = value }
    }

    /// A keystroke in the number field, or a pick from the contact list.
    ///
    /// Normalised on every one through C095's ``PhoneNumber``, so a phonebook entry stored as
    /// `077 123 4567` and one stored as `+94 77 123 4567` are the same rider. The registration
    /// answer is dropped whenever the number changes — it was about a different person.
    func onPhoneChanged(_ value: String) {
        let normalised = PhoneNumber.normalise(value)
        state.riderPhone = normalised
        state.riderRegistered = nil
        state.errorKey = nil
        draft.update { $0.riderPhone = normalised }
        if PhoneNumber.isValid(normalised) { lookUp(normalised) }
    }

    /// The Search / Map pin / Paste link / Request segmented control.
    func setMethod(_ method: PickupMethod) {
        state.method = method
        state.errorKey = nil
    }

    /// Whatever the chosen method produced — a search result, a dropped pin, or a pasted link.
    func setPickup(_ place: Place) {
        state.pickup = place
        draft.update { $0.pickup = place }
    }

    /// *"Request rider location"* — the FCM round trip (P-02).
    ///
    /// The rider is subscribed to over SignalR **before** the countdown starts, so an answer that
    /// arrives in the first second is not missed while this is still setting up.
    func requestRiderLocation() {
        guard state.canRequestLocation else { return }
        let phone = state.riderPhone

        work.append(Task {
            do {
                let created = try await bookings.requestRiderLocation(phone: PhoneNumber.toE164(phone))
                live.watchLocationRequest(created.requestId)
                state.requestId = created.requestId
                state.requestState = created.state
                state.secondsLeft = Int(created.ttl)
                state.errorKey = nil

                if created.state == LocationRequestState.riderNotRegistered {
                    // P-03 again, this time from ride-svc's own view. The lookup can say
                    // "registered" and the create still refuse — a user deleted between the two.
                    state.riderRegistered = false
                    return
                }
                countDown(requestId: created.requestId, from: Int(created.ttl))
                await awaitResolution(created.requestId)
            } catch is CancellationError {
                return
            } catch {
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// P-03 — is there anybody to send an FCM to?
    private func lookUp(_ phone: String) {
        state.isLooking = true
        work.append(Task {
            guard let registered = try? await bookings.isRegistered(phone: PhoneNumber.toE164(phone)) else {
                // Unknown rather than unregistered. Leaving `nil` keeps every method available,
                // which is the safe direction: the worst case is a request that expires.
                state.isLooking = false
                return
            }
            guard !Task.isCancelled else { return }
            state.riderRegistered = registered
            state.isLooking = false
            // US-8.19: an unregistered rider cannot be asked, so the screen moves the booker to a
            // method that works rather than leaving a dead button selected.
            if !registered, state.method == .request { state.method = .search }
        })
    }

    /// The wireframe's `5:00`, ticking. Stops the moment the request resolves.
    private func countDown(requestId: String, from ttl: Int) {
        work.append(Task {
            var left = ttl
            while left > 0, state.requestState == LocationRequestState.pending, !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                left -= 1
                guard state.requestId == requestId else { return }
                state.secondsLeft = max(left, 0)
            }
            if state.requestState == LocationRequestState.pending {
                onResolved(requestId: requestId, state: LocationRequestState.expired)
            }
        })
    }

    /// The fallback poll.
    ///
    /// The socket is the primary channel and usually wins. This exists for the booker who
    /// backgrounded the app, lost the connection and came back — ``PassengerLiveMap`` rejoins the
    /// group on reconnect but the event that fired while it was away is gone, and
    /// `GET /v1/location-requests/{id}` is the contract's own answer to exactly that (*"the poll
    /// exists for reconnect and support diagnosis"*).
    private func awaitResolution(_ requestId: String) async {
        while state.requestState == LocationRequestState.pending, !Task.isCancelled {
            try? await Task.sleep(nanoseconds: Self.pollIntervalNanoseconds)
            guard !Task.isCancelled else { return }
            await pollOnce(requestId)
        }
    }

    /// One read. A failure is not fatal — the socket may still deliver, and the countdown ends this.
    private func pollOnce(_ requestId: String) async {
        guard let answer = try? await bookings.locationRequest(requestId: requestId) else { return }
        guard !Task.isCancelled, answer.state != LocationRequestState.pending else { return }
        if let geo = answer.geo {
            setPickup(Place(lat: geo.lat, lng: geo.lng, address: nil))
        }
        onResolved(requestId: requestId, state: answer.state)
    }

    /// The request reached a terminal state, from either channel.
    ///
    /// A **Confirmed** carries the pin, which is what auto-fills the pickup. A **Declined** carries
    /// nothing at all and must not be made to look as though it did (P-02): the screen falls back to
    /// the other three methods.
    private func onResolved(requestId: String, state resolved: LocationRequestState) {
        guard state.requestId == requestId else { return }
        state.requestState = resolved
        state.secondsLeft = 0

        if resolved != LocationRequestState.confirmed, state.pickup == nil {
            state.method = .search
        }
    }

    /// How often the fallback poll runs.
    ///
    /// Ten seconds over a five-minute window is thirty reads at worst, and only for a booker whose
    /// socket is not delivering. Faster would be polling a channel that already works.
    private static let pollIntervalNanoseconds: UInt64 = 10_000_000_000
}
