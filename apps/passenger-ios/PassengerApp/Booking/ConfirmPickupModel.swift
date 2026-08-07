import Combine
import Foundation
import MageRideShared

/// SCR-PI-011's state.
struct ConfirmPickupState {

    /// Where the rider says they are. Seeded from their own fix and **dragged** from there — the
    /// wireframe's *"drag to adjust"*.
    var pin: GeoPoint?

    /// The fix's accuracy, sent with a Share. It is what tells dispatch how much to trust the point;
    /// dropping it would make a 500 m cell-tower fix look like a GPS lock.
    var accuracyMetres: Double = 0

    /// The five-minute TTL, counting down. `0` auto-dismisses.
    var secondsLeft = 0

    var isSending = false

    /// Set once, terminally. The screen closes on it.
    var outcome: LocationRequestState?

    var errorKey: String?

    /// The wireframe's `4:38`.
    var countdown: String {
        String(format: "%d:%02d", secondsLeft / 60, secondsLeft % 60)
    }

    /// Whether Share can fire — there has to be a point to share.
    var canShare: Bool { pin != nil && !isSending && outcome == nil }
}

/// SCR-PI-011 — the **rider's** side, and the one screen in this app where privacy is the feature.
///
/// **Declining sends no coordinates. None.** P-02 says so, `ride.yaml` enforces it — the decline
/// operation takes no body at all — and this type makes it structural: ``decline()`` calls
/// ``BookingRepository/declineLocationRequest(requestId:)``, which has no parameter to put a point
/// in. There is no *"approximate location"* consolation, no city-level fallback, and the banner says
/// so out loud before the rider decides.
///
/// **The rider is not necessarily a passenger of this app's booker.** They received a silent FCM data
/// message (`{kind:'location_request', requestId, bookerName, ttl}` — no deep link, see
/// ``PushRouter``), and this screen is all they see of the booking.
@MainActor
final class ConfirmPickupModel: ObservableObject {

    @Published private(set) var state = ConfirmPickupState()

    private let requestId: String
    private let bookings: BookingRepository
    private let locations: PassengerLocationSource
    private let now: () -> Date

    private var subscriptions: Set<AnyCancellable> = []
    private var work: [Task<Void, Never>] = []

    init(
        requestId: String,
        bookings: BookingRepository,
        locations: PassengerLocationSource,
        now: @escaping () -> Date = Date.init
    ) {
        self.requestId = requestId
        self.bookings = bookings
        self.locations = locations
        self.now = now
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Reads the request and subscribes to the rider's own fix. Idempotent.
    func start() {
        guard subscriptions.isEmpty else { return }

        // The rider's own fix seeds the pin. They can drag it from there, which is what the
        // wireframe's "drag to adjust" is for — a GPS lock indoors is often a building away.
        locations.fixes
            .sink { [weak self] fix in
                guard let self else { return }
                if self.state.pin == nil { self.state.pin = GeoPoint(lat: fix.lat, lng: fix.lng) }
                self.state.accuracyMetres = fix.accuracyMetres
            }
            .store(in: &subscriptions)

        work.append(Task { await load() })
    }

    /// The pin was dragged. SCR-PI-011's picker moves the **map** under a fixed marker, so this is
    /// the camera's resting centre — see ``MageRideMap`` `onCameraIdle`.
    func onPinMoved(_ point: GeoPoint) {
        state.pin = point
    }

    /// *"Share location"*.
    ///
    /// The accuracy travels with the point because `GeoPointWithAccuracy` has a field for it and
    /// dispatch uses it: a 500 m cell-tower fix and a 5 m GPS lock are different instructions to a
    /// driver, and sending only the coordinate would make them look identical.
    func share() {
        guard state.canShare, let pin = state.pin else { return }

        state.isSending = true
        state.errorKey = nil
        let accuracy = state.accuracyMetres

        work.append(Task {
            do {
                let answer = try await bookings.confirmLocationRequest(
                    requestId: requestId,
                    at: IosBookingRequestsKt.geoPointWithAccuracy(
                        lat: pin.lat,
                        lng: pin.lng,
                        accuracyMetres: accuracy
                    )
                )
                state.isSending = false
                state.outcome = answer.state
            } catch is CancellationError {
                state.isSending = false
            } catch {
                state.isSending = false
                state.errorKey = BookingErrors.messageKey(for: error)
            }
        })
    }

    /// *"Decline"* — and nothing else leaves the device.
    ///
    /// Note what is **not** here: no `pin`, no `accuracyMetres`, no last-known anything. Neither is
    /// read. That is the point of P-02, and it is why declining is a separate operation rather than a
    /// confirm with a flag.
    func decline() {
        guard state.outcome == nil else { return }
        state.isSending = true
        state.errorKey = nil

        work.append(Task {
            do {
                let answer = try await bookings.declineLocationRequest(requestId: requestId)
                state.isSending = false
                state.outcome = answer.state
            } catch is CancellationError {
                state.isSending = false
            } catch {
                // The refusal still stands locally: the screen closes and no position was sent. A
                // rider must never be left on a "share" screen because a decline failed — and
                // nothing about the failure changes what was (not) sent.
                state.isSending = false
                state.outcome = LocationRequestState.declined
                state.errorKey = nil
            }
        })
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// Reads the request, and starts the clock from **its** expiry rather than from a fresh 300 s.
    ///
    /// The FCM may have sat in a low-power bucket for two minutes before the handset woke up; a
    /// countdown that started at 5:00 on open would promise time the server has already spent, and
    /// the rider would tap Share into a `410`.
    private func load() async {
        do {
            let request = try await bookings.locationRequest(requestId: requestId)
            guard !Task.isCancelled else { return }

            // Through `IosInstantKt` rather than `Instant.toEpochMilliseconds()` directly: reading
            // one that way does cross the bridge, but the helper is the door this app spells it at
            // so no call site has to know that.
            let millis = IosInstantKt.timestampEpochMillis(instant: request.expiresAt)
            let expiry = Date(timeIntervalSince1970: Double(millis) / 1000)
            state.secondsLeft = max(Int(expiry.timeIntervalSince(now())), 0)
            state.outcome = request.state == LocationRequestState.pending ? nil : request.state
            if state.outcome == nil { countDown() }
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = BookingErrors.messageKey(for: error)
        }
    }

    /// The banner's `4:38`. On zero the request is gone and the screen dismisses itself.
    private func countDown() {
        work.append(Task {
            while state.secondsLeft > 0, state.outcome == nil, !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                state.secondsLeft = max(state.secondsLeft - 1, 0)
            }
            if state.outcome == nil, !Task.isCancelled {
                state.outcome = LocationRequestState.expired
            }
        })
    }
}
