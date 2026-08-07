import Foundation
import MageRideShared

// NAME CLASH, AND IT IS A REAL ONE. `:shared`'s `lk.mageride.shared.domain.dispatch.DriverStanding`
// — the level, the points and the report counter — reaches Swift as `DriverStanding`, and this app
// ALREADY has a struct of that name: C088's `Home/StandbyRepository.swift` calls the dashboard's
// whole status header a `DriverStanding` too. Swift resolves an unqualified name against the local
// module first, so `DriverStanding` in this target means C088's struct and nothing warns. Every
// reference to the Kotlin type in this cluster is therefore spelled `MageRideShared.DriverStanding`.

/// Everything SCR-DI-019 prints, and the one fact SCR-DI-017 gates on.
///
/// - Parameters:
///   - standing: The level and the points banked toward the next one; `nil` while reputation has not
///     answered. **Never defaulted** — see ``ApiJobsRepository/standing(driverId:)``.
///   - level: The raw `GET …/level` response, kept so the server's own `levelUpThreshold` reaches
///     the rules (US-14.12).
///   - acceptanceRate: `0.0`–`1.0` (US-6A.14).
///   - noShows: Scheduled-ride no-shows, each of which cost a level (US-6A.7).
///   - points: Rating points banked, from whichever read answered.
struct JobStanding {

    var standing: MageRideShared.DriverStanding?
    var level: DriverLevelResponse?
    var acceptanceRate: Double?
    var noShows: Int?
    var points: Int?

    /// The empty standing — *"reputation has not answered"*, which is US-6A.8's third value and the
    /// state ``DriverLevelState`` starts in.
    init(
        standing: MageRideShared.DriverStanding? = nil,
        level: DriverLevelResponse? = nil,
        acceptanceRate: Double? = nil,
        noShows: Int? = nil,
        points: Int? = nil
    ) {
        self.standing = standing
        self.level = level
        self.acceptanceRate = acceptanceRate
        self.noShows = noShows
        self.points = points
    }

    /// The level rules to evaluate against — the server's threshold when there is one (US-14.12).
    ///
    /// Through ``driverLevelRulesFor(level:)`` rather than `DriverLevelRules(config:)` directly: the
    /// config argument is **defaulted** and a Kotlin default does not survive the export, so reaching
    /// the rule from Swift would mean spelling D5' §4.2's 500 and its `jobBoardMinLevel` here — a
    /// second copy of the two numbers `PUT /v1/admin/drivers/level-config` exists to move.
    var rules: DriverLevelRules { IosJobBoardKt.driverLevelRulesFor(level: level) }

    /// **US-6A.8's gate.** Whether the Job Board is open to this driver.
    ///
    /// `nil` while the level read is in flight or after one that failed, so a screen can hold the
    /// shimmer rather than choosing between the gate state and the list on no information. The third
    /// value is the one that could do harm: rendering the gate on `nil` would tell a Level-3 driver
    /// whose level read timed out that they are Level 1.
    var hasJobBoardAccess: Bool? {
        standing.map { rules.hasJobBoardAccess(standing: $0) }
    }

    /// The lowest level the board opens at, from the server's config when it sent one.
    var jobBoardMinLevel: Int { Int(rules.jobBoardMinLevel) }

    /// The standing with the counters the stats read supplies folded in, for the level screen.
    var detailed: MageRideShared.DriverStanding? {
        guard let standing else { return nil }
        guard let points, points != Int(standing.points) else { return standing }
        return standing.doCopy(
            level: standing.level,
            points: Int32(points),
            passengerReports: standing.passengerReports,
            isDelisted: standing.isDelisted
        )
    }
}

/// dispatch-svc as SCR-DI-017, SCR-DI-018 and SCR-DI-019 use it.
///
/// **The board, the upcoming list and the level are one service and one repository**, because the L1
/// gate joins them: `GET /v1/rides/job-board` is only worth calling for a driver
/// `DriverLevelRules.hasJobBoardAccess` answers `true` for (US-6A.8), and that answer comes from
/// `GET /v1/drivers/{driverId}/level`. Splitting them would put the gate in a screen.
///
/// **Nothing here decides a ride's fate.** The board is post-intent only — there is no accept route
/// on dispatch-svc and there is not meant to be (D5' §3.7): at T-30 min the ride is dispatched to the
/// closest intent-poster by Level as an ordinary offer, which SCR-DI-014 accepts through ride-svc.
/// R-01's fence, seen from the client side.
///
/// A protocol for the reason every seam in this target is one: `DispatchApi` is a Kotlin interface
/// and cannot be stood in for from Swift, so a model test with no gateway needs the seam on this side
/// of the bridge.
protocol JobsRepository: AnyObject {

    /// The Driver Level and the US-6A.14 counters, as one standing.
    func standing(driverId: String) async -> JobStanding

    /// `GET /v1/rides/job-board` — every future scheduled ride within the D-06 catchment.
    func board(lat: Double, lng: Double, radiusMetres: Int) async throws -> [ScheduledRide]

    /// `POST /v1/rides/job-board/{rideId}/intent` — register interest (US-6A.5).
    func postIntent(scheduledRideId: String) async throws

    /// `GET /v1/rides/scheduled/{driverId}` — SCR-DI-018's list, ordered by pickup time.
    func upcoming(driverId: String) async throws -> [ScheduledRide]

    /// `DELETE /v1/rides/schedule/{scheduledRideId}` — withdraw a booking before it is dispatched.
    func cancelScheduled(scheduledRideId: String) async throws
}

/// ``JobsRepository`` over `:shared`'s typed dispatch client.
final class ApiJobsRepository: JobsRepository {

    private let dispatch: DispatchApi

    init(dispatch: DispatchApi) {
        self.dispatch = dispatch
    }

    /// The two reads are independent and each is best-effort: a driver whose stats read fails still
    /// needs the level, because the level is what opens or closes the Job Board. A level that could
    /// not be read is `nil` rather than `DriverLevelRules.STARTING_LEVEL` — guessing 3 would open the
    /// board to a driver the server has demoted.
    func standing(driverId: String) async -> JobStanding {
        let level = await read { try await self.dispatch.getDriverLevel(driverId: driverId) }
        let stats = await read { try await self.dispatch.getDriverStats(driverId: driverId) }

        return JobStanding(
            standing: level.map { MageRideShared.DriverStanding.companion.of(response: $0) },
            level: level,
            acceptanceRate: stats?.acceptanceRate,
            noShows: stats.map { Int($0.noShows) },
            // `GET …/level` answers `ratingPoints`, `GET …/stats` answers `points`; they are the same
            // counter. The stats read wins when both answered, because it is the endpoint D3' files
            // US-6A.14 under.
            points: stats.map { Int($0.points) } ?? level?.ratingPoints.map { Int($0.int32Value) }
        )
    }

    /// The radius is `JobBoard.CATCHMENT_METRES` and is sent explicitly: the contract's own default
    /// is the same 30 km, but the screen prints the number and one that showed one radius while
    /// asking for another would be lying quietly.
    func board(lat: Double, lng: Double, radiusMetres: Int) async throws -> [ScheduledRide] {
        try await dispatch.listJobBoard(
            lat: lat,
            lng: lng,
            radiusMetres: KotlinInt(value: Int32(radiusMetres)),
            page: PageRequest.companion.FIRST
        ).items
    }

    /// The path parameter is the **`dispatch.scheduled_rides` id**, which is what the board row
    /// carries; the contract spells it `rideId` and the service says so at the endpoint.
    func postIntent(scheduledRideId: String) async throws {
        _ = try await dispatch.postJobBoardIntent(rideId: scheduledRideId, idempotencyKey: nil)
    }

    func upcoming(driverId: String) async throws -> [ScheduledRide] {
        try await dispatch.listDriverScheduledRides(driverId: driverId, page: PageRequest.companion.FIRST).items
    }

    /// **This route requires the `passenger` role** (`ScheduledRideEndpoints`, which splits the group
    /// by role: the passenger books and cancels, the driver browses and posts intent), so a driver's
    /// call is a `403`. It is wired anyway rather than left out, because that is the only cancellation
    /// route `dispatch.yaml` has and the refusal is the honest answer to the deliverable — see the
    /// C072 handoff, spec gap 4, carried forward unchanged.
    func cancelScheduled(scheduledRideId: String) async throws {
        try await dispatch.cancelScheduledRide(scheduledRideId: scheduledRideId)
    }

    /// Attempts [block], answering `nil` for anything that throws.
    ///
    /// A dead stats read must not close the Job Board. Nothing is logged or surfaced: an acceptance
    /// figure that is absent for a minute is a figure that is absent, and a board that refused to
    /// draw because reputation was down would be a driver who cannot look for work.
    private func read<T>(_ block: () async throws -> T) async -> T? {
        try? await block()
    }
}
