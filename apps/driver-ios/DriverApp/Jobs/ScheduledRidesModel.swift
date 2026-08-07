import Foundation
import MageRideShared

/// One row of SCR-DI-018.
///
/// - Parameters:
///   - ride: The upcoming booking.
///   - pickupAt: When the passenger expects to be collected.
///   - goesLiveAt: T-30 for this pickup, from `:shared` — see ``ScheduledRidesModel``.
///   - at: The clock this row is currently rendered against, advanced once a second.
///   - isCancelling: A withdrawal is in flight for this row.
struct ScheduledRideRow: Identifiable {

    let ride: ScheduledRide
    let pickupAt: Date
    let goesLiveAt: Date
    var at: Date
    var isCancelling = false

    /// The `dispatch.scheduled_rides` id.
    var id: String { ride.scheduledRideId }

    /// How long until the pickup, floored at zero.
    var secondsToPickup: Int64 { max(Int64(pickupAt.timeIntervalSince(at)), 0) }

    /// Minutes left, for the *"in 28 min"* pill.
    var minutesToPickup: Int { Int(secondsToPickup / 60) }

    /// **US-6A.15.** Whether the 30-minute reminder is due, which is the wireframe's *"reminder
    /// fired"* note and its amber *"in 28 min"* pill.
    ///
    /// A comparison against ``goesLiveAt`` rather than a threshold of this screen's own: the
    /// reminder and the board's go-live are the **same instant** (D5' §3.7 dispatches at T-30; §14.4
    /// pushes `SCHEDULED_REMINDER` at 30 min for a driver), so both screens read
    /// `JobBoard.GO_LIVE_LEAD` through the one helper and neither keeps a number. Nothing on the
    /// wire records that a push was actually sent, so there is no flag to prefer to this.
    var hasReminderFired: Bool { at >= goesLiveAt }

    /// Whether this row is still a booking rather than a ride dispatch has already materialised.
    var isScheduled: Bool { ride.status == ScheduledRideStatus.scheduled }
}

/// SCR-DI-018's state.
///
/// - Parameters:
///   - isLoading: The read is in flight.
///   - rows: The driver's upcoming rides, soonest first.
///   - errorKey: Resolved copy for the last failure.
struct ScheduledRidesState {

    var isLoading = true
    var rows: [ScheduledRideRow] = []
    var errorKey: String?

    /// Nothing upcoming — the list's own empty state.
    var isEmpty: Bool { !isLoading && rows.isEmpty }
}

/// **SCR-DI-018 · scheduled rides** (US-6A.15).
///
/// `GET /v1/rides/scheduled/{driverId}` — *"rides this driver has been assigned, ordered by pickup
/// time"*. The list is re-sorted here anyway, because the order is what the screen's whole meaning
/// rests on and a server that stopped sorting would be invisible.
///
/// **A no-show on one of these costs a level** (US-6A.7, D5' §4.2). That is the reason the countdown
/// ticks rather than being read once: the row that says *"in 28 min"* is the one a driver has to act
/// on, and a screen left open on a stale minute count is how the level is lost.
///
/// ### Cancellation
///
/// The deliverable asks for it and `dispatch.yaml` has exactly one route for it —
/// `DELETE /v1/rides/schedule/{scheduledRideId}` — which dispatch-svc maps inside the **passenger**
/// role group. A driver's call is therefore a `403`, and once dispatch has materialised the ride the
/// same route answers `409` for anybody, because cancellation belongs to ride-svc's penalty matrix
/// from that point on. The button is wired, disabled once the row is `DISPATCHED`, and renders the
/// server's refusal as copy rather than pretending to have done something. See the C072 handoff.
@MainActor
final class ScheduledRidesModel: ObservableObject {

    @Published private(set) var state = ScheduledRidesState()

    private let identity: DriverIdentity
    private let jobs: JobsRepository
    private let now: () -> Date

    private var ticker: Task<Void, Never>?

    /// - Parameter now: The clock. Injected for ``JobBoardModel``'s reason — the countdown and the
    ///   T-30 reminder are this screen's whole behaviour, and a test that could only wait for real
    ///   time would have to sleep for half an hour.
    init(identity: DriverIdentity, jobs: JobsRepository, now: @escaping () -> Date = Date.init) {
        self.identity = identity
        self.jobs = jobs
        self.now = now
    }

    deinit {
        ticker?.cancel()
    }

    /// Starts the one-second countdown. Idempotent.
    func start() {
        guard ticker == nil else { return }
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: ScheduledRidesModel.tickNanoseconds)
                guard !Task.isCancelled, let self else { return }
                self.tick()
            }
        }
    }

    func stop() {
        ticker?.cancel()
        ticker = nil
    }

    /// Re-reads the upcoming list.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.errorKey = nil

        do {
            let rides = try await jobs.upcoming(driverId: driverId)
            state.rows = rows(from: rides)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// Withdraws a booking that has not been dispatched yet.
    ///
    /// Refused for a `DISPATCHED` row before the call is made: from T-30 the ride exists and
    /// `POST /v1/rides/{rideId}/cancel` owns the outcome, penalties and all.
    func cancel(_ row: ScheduledRideRow) async {
        guard row.isScheduled, !row.isCancelling else { return }
        mark(row.id, isCancelling: true)

        do {
            try await jobs.cancelScheduled(scheduledRideId: row.id)
            state.rows.removeAll { $0.id == row.id }
        } catch {
            mark(row.id, isCancelling: false)
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// Advances every countdown once a second.
    ///
    /// One clock on the row rather than a recomputed duration: *"in 28 min"* and *"the reminder has
    /// fired"* are two readings of the same instant, and a tick that moved only one of them would
    /// eventually show an amber pill on a card whose note said nothing had been sent.
    private func tick() {
        guard !state.rows.isEmpty else { return }
        let at = now()
        state.rows = state.rows.map { row in
            var updated = row
            updated.at = at
            return updated
        }
    }

    private func rows(from rides: [ScheduledRide]) -> [ScheduledRideRow] {
        let at = now()
        return rides
            .filter { $0.status != ScheduledRideStatus.cancelled }
            .map { ride in
                ScheduledRideRow(
                    ride: ride,
                    pickupAt: ScheduleLabels.instant(ride.pickupTime),
                    goesLiveAt: Date(
                        timeIntervalSince1970: TimeInterval(IosJobBoardKt.jobBoardGoesLiveAtMillis(ride: ride)) / 1000
                    ),
                    at: at
                )
            }
            .sorted { $0.pickupAt < $1.pickupAt }
    }

    private func mark(_ id: String, isCancelling: Bool) {
        state.rows = state.rows.map { row in
            guard row.id == id else { return row }
            var updated = row
            updated.isCancelling = isCancelling
            return updated
        }
    }

    /// The countdown is a minute figure, but the minute it turns over on has to be the right one.
    private static let tickNanoseconds: UInt64 = 1_000_000_000
}
