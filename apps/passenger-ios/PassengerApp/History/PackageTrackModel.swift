import Combine
import Foundation
import MageRideShared

/// Which end of the parcel this handset is.
///
/// **Decided from the ride, not from the URI.** `mageride://package/{rideId}` is the same link for
/// both parties — notification-svc sends it to the recipient on `package_picked_up` and to the sender
/// on `package_delivered` — so which screen to draw is a fact about who is holding the phone: the
/// sender is the ride's booker, the recipient is the number on `recipientPhone`.
/// ``PassengerRoute/packageTracking(rideId:)``'s own note says C099 makes this call.
enum PackageParty {
    case sender
    case recipient
}

/// SCR-PI-020 and SCR-PI-021's state.
struct PackageTrackState {

    /// The aggregate. `nil` until the first read lands.
    var ride: RideDetail?

    /// Which of the two screens this is. Defaults to ``PackageParty/sender`` because that is the
    /// party whose screen is reachable *without* a push — a booker taps their own parcel — and
    /// because the header is the only thing it decides before the read lands.
    var party: PackageParty = .sender

    /// US-20.7's handoff progress. `nil` before the first read.
    var status: PackageStatus?

    /// The assigned driver's live marker, from the SignalR plane.
    var driverPosition: GeoPoint?

    /// The code **this party** reads out — pickup for a sender, delivery for a recipient. `nil` when
    /// the app was never told it; see ``PackageOtps`` for why there is no read.
    var otp: String?

    var errorKey: String?

    /// The wireframe's `Pickup pending → Picked up → In transit → Delivered`, as an index.
    ///
    /// `nil` reads as the first step rather than as *"unknown"*: before the driver has confirmed
    /// anything a pickup **is** pending, and an empty bar would look like a screen that failed.
    ///
    /// Compared against the three singletons rather than looked up in a list, which is
    /// ``ModeToken/forMode(_:)``'s idiom and for its reason: an exported Kotlin enum entry is **one
    /// object**, `==` is `isEqual:` over it, and a generic constraint that needs `Equatable` on one —
    /// which `firstIndex(of:)` and `switch` over an `Optional` both want — is a question better not
    /// asked on a host that cannot compile the answer (the C096 finding, and
    /// ``PaymentMethodScreen``'s note about `onChange(of:)`).
    var step: Int {
        guard let status else { return 0 }
        if status == PackageStatus.pickedUp { return 1 }
        if status == PackageStatus.inTransit { return 2 }
        if status == PackageStatus.delivered { return PackageTrackState.deliveredStep }
        return 0
    }

    /// Whether the handover is done and there is nothing left to read out.
    var isDelivered: Bool { step == PackageTrackState.deliveredStep }

    /// The driver's real number (AL-48).
    ///
    /// **On a package `counterpartyPhone` is the *far end*** — for a sender that is the recipient —
    /// so it is not what this screen dials. `ride.yaml` carries `senderPhone` and `recipientPhone`
    /// separately for exactly that reason, and neither of them is the driver either. See
    /// ``PackageTrackModel`` for the whole of that gap.
    var driverPhone: String? { ride?.counterpartyPhone }

    /// The wireframe's `ETA 12 min` under the driver's name. `nil` before there is one to state.
    var etaMinutes: Int32? {
        guard let seconds = ride?.driver?.etaSeconds?.int32Value, seconds > 0 else { return nil }
        return (seconds + 59) / 60
    }

    /// US-20.7's four captions, in order. The bar and ``step`` cannot disagree about how many there
    /// are, because this is the only list of them.
    static let stepKeys = [
        "package_step_pending",
        "package_step_picked_up",
        "package_step_in_transit",
        "package_step_delivered",
    ]

    /// The last index of ``stepKeys`` — the handover, after which nothing moves again.
    static let deliveredStep = 3
}

/// SCR-PI-020 / SCR-PI-021 — a parcel, from both ends.
///
/// **One model for two screens because it is one ride.** The sender and the recipient watch the same
/// `rides.rides` row through the same `ride:{rideId}` SignalR group; what differs is which OTP is
/// theirs to read out and what the header says. Two models would be two subscriptions to one group.
///
/// **The recipient never signed in to get here** (P-09, AL-45). They arrived from a `package_picked_up`
/// push, or — with no app — from an SMS onto the SCR-WT web page, which is not this app's problem.
/// Nothing on this screen is reachable by signing in and looking for it.
///
/// **The socket is the channel and the poll is the floor**, the same split ``ActiveRideModel`` makes:
/// `PackageStatusChanged` moves the bar and `DriverPosition` moves the marker, and a fifteen-second
/// re-read exists because a parcel is the one thing on this surface whose progress a passenger is
/// standing in a doorway waiting for. The poll **stops** once the parcel has been handed over.
@MainActor
final class PackageTrackModel: ObservableObject {

    @Published private(set) var state = PackageTrackState()

    private let rideId: String
    private let history: HistoryRepository
    private let live: PassengerLiveMap
    private let otps: PackageOtps

    /// Who this handset belongs to. `nil` for a recipient, who has no session at all — which is why
    /// the *absence* of a match is the answer rather than a missing case.
    private let signedInUserId: String?

    /// How often the poll re-reads. Injected so a test can assert the floor without sleeping through
    /// it — the same reason ``ActiveRideModel`` takes its interval.
    private let pollInterval: TimeInterval

    private var subscriptions: Set<AnyCancellable> = []
    private var work: [Task<Void, Never>] = []
    private var isStarted = false

    init(
        rideId: String,
        history: HistoryRepository,
        live: PassengerLiveMap,
        otps: PackageOtps,
        signedInUserId: String?,
        pollInterval: TimeInterval = 15
    ) {
        self.rideId = rideId
        self.history = history
        self.live = live
        self.otps = otps
        self.signedInUserId = signedInUserId
        self.pollInterval = pollInterval
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Joins the ride's group, reads it, and starts the socket and the poll.
    ///
    /// Idempotent, so the screen can call it from `.task` and a re-entry after a scene change costs
    /// nothing. `SubscribeRide` is the **caller's own** ride (`signalr-hub.md` §2.1) and is rejoined
    /// by ``PassengerLiveMap`` on every reconnect.
    func start() {
        guard !isStarted else { return }
        isStarted = true

        live.watchRide(rideId)
        watchEvents()

        work.append(Task { await self.refresh() })
        work.append(Task { await self.poll() })
    }

    /// Leaves the group and stops every loop. The screen went away.
    func stop() {
        live.stopWatchingRide(rideId)
        work.forEach { $0.cancel() }
        work = []
        subscriptions = []
        isStarted = false
    }

    /// Re-reads the aggregate and re-decides which party this is.
    func refresh() async {
        do {
            let ride = try await history.ride(rideId: rideId)
            guard !Task.isCancelled else { return }

            let party = PackageTrackModel.party(of: ride, signedInUserId: signedInUserId)
            state.ride = ride
            state.party = party
            state.status = ride.packageStatus
            // Each party reads out their own code and never the other's: the pickup OTP proves the
            // driver collected from the right sender, the delivery OTP proves they handed it to the
            // right recipient (P-07, US-20.4/20.5).
            state.otp = party == .sender ? otps.pickupFor(rideId: rideId) : otps.deliveryFor(rideId: rideId)
            state.errorKey = nil
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = RideErrors.messageKey(for: error)
        }
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    /// Which end of the parcel a handset with this session is.
    ///
    /// `static` and pure so the rule is assertable without a socket. **The booker is the sender**:
    /// a package is booked by whoever is sending it (P-06), and a recipient — who may never have used
    /// this app before — has no booking of their own to match against.
    static func party(of ride: RideDetail, signedInUserId: String?) -> PackageParty {
        guard let signedInUserId, let bookerId = ride.bookerId, bookerId == signedInUserId else {
            return .recipient
        }
        return .sender
    }

    /// The socket. `PackageStatusChanged` is US-20.7's bar moving; `DriverPosition` is the marker.
    ///
    /// The status is folded in directly rather than re-read, unlike ``ActiveRideModel``'s ride
    /// transitions: `PackageStatusChanged` carries the whole of what changed — a ride id and a status
    /// — and there is nothing else on the screen it could have moved.
    private func watchEvents() {
        live.events
            .sink { [weak self] event in
                guard let self else { return }
                switch event {
                case .packageStatus(let payload) where payload.rideId == rideId:
                    state.status = payload.status

                case .driverMoved(let payload) where payload.rideId == rideId:
                    state.driverPosition = GeoPoint(lat: payload.lat, lng: payload.lng)

                default:
                    break
                }
            }
            .store(in: &subscriptions)
    }

    /// The floor under the socket, and it stops once the parcel has been handed over.
    ///
    /// A delivered parcel does not change again, and a screen that kept polling one would be a
    /// background request every fifteen seconds for as long as the app lived.
    private func poll() async {
        while !Task.isCancelled, !state.isDelivered {
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            guard !Task.isCancelled else { return }
            await refresh()
        }
    }
}
