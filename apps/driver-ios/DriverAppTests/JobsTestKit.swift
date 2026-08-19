import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures cluster 3's board, level and earnings models are driven by.
///
/// Same rule as ``DashboardTestKit``: every seam here is a **Swift protocol**, because the Kotlin
/// types behind them are interfaces Swift cannot stand in for. The DTOs underneath are the real
/// shared ones — a fixture is built with the same initialiser the gateway's response deserialises
/// into, so a contract change fails these tests rather than a driver's phone.
///
/// **The clock is a value, not the wall.** Every model in this cluster takes a `now` closure, because
/// its whole behaviour is a comparison against T-30 (`JobBoard.GO_LIVE_LEAD`) or against a Colombo
/// midnight, and a test that could only wait for real time would have to sleep for half an hour.

let testScheduledRideId = "01JSCHED0000000000000001"

/// A fixed instant well inside a Colombo working day — **2026-06-17 14:00 Colombo** (08:30 UTC).
///
/// Chosen rather than `Date()` so *"Today"*, *"Tomorrow"* and the hour buckets are the same on every
/// run and in every timezone the tests happen to be executed in. Mid-afternoon, so an hourly chart
/// has room for several buckets before it.
let testNow = Date(timeIntervalSince1970: 1_781_685_000)

/// **19:00 UTC on the same day, which is already the 18th in Colombo** (00:30).
///
/// The mirror of `apps/driver-android`'s `Fixtures.MIDNIGHT_EDGE`: anything bucketed or labelled in
/// UTC — or in the handset's zone — lands on the wrong day here, and nothing read in Colombo does.
let testMidnightEdge = Date(timeIntervalSince1970: 1_781_722_800)

func timestamp(_ date: Date) -> Timestamp {
    IosInstantKt.timestampFromEpochMillis(millis: Int64(date.timeIntervalSince1970 * 1000))
}

/// A board row or an upcoming ride.
///
/// [pickupIn] is measured from ``testNow``, so a caller writes the one thing the rule turns on —
/// how far the pickup is from the clock the model was given.
func scheduledRide(
    id: String = testScheduledRideId,
    pickupIn: TimeInterval,
    from: Date = testNow,
    status: ScheduledRideStatus = ScheduledRideStatus.scheduled,
    vehicleType: RideVehicleType = RideVehicleType.sedan,
    pickupAddress: String? = "Maharagama",
    dropoffAddress: String? = "Fort",
    distanceM: Int32? = 11_200,
    intentCount: Int32? = 0
) -> ScheduledRide {
    ScheduledRide(
        scheduledRideId: id,
        rideId: nil,
        pickup: Place(lat: testHere.lat, lng: testHere.lng, address: pickupAddress),
        dropoff: Place(lat: testThere.lat, lng: testThere.lng, address: dropoffAddress),
        vehicleType: vehicleType,
        pickupTime: timestamp(from.addingTimeInterval(pickupIn)),
        status: status,
        distanceM: distanceM.map { KotlinInt(value: $0) },
        intentCount: intentCount.map { KotlinInt(value: $0) }
    )
}

func levelResponse(level: Int32 = 3, ratingPoints: Int32? = 310, levelUpThreshold: Int32? = nil) -> DriverLevelResponse {
    DriverLevelResponse(
        level: level,
        ratingPoints: ratingPoints.map { KotlinInt(value: $0) },
        levelUpThreshold: levelUpThreshold.map { KotlinInt(value: $0) }
    )
}

/// A standing as ``JobsRepository`` assembles one, with the level read having answered.
func jobStanding(
    level: Int32 = 3,
    points: Int32? = 310,
    levelUpThreshold: Int32? = nil,
    acceptanceRate: Double? = 0.92,
    noShows: Int? = 1
) -> JobStanding {
    let response = levelResponse(level: level, ratingPoints: points, levelUpThreshold: levelUpThreshold)
    return JobStanding(
        standing: DriverStanding.companion.of(response: response),
        level: response,
        acceptanceRate: acceptanceRate,
        noShows: noShows,
        points: points.map { Int($0) }
    )
}

/// A standing where **reputation did not answer** — US-6A.8's third value.
func unreadStanding() -> JobStanding { JobStanding() }

func earningsSummary(
    period: EarningsPeriod = EarningsPeriod.today,
    rangeFrom: BusinessDate? = nil,
    rangeTo: BusinessDate? = nil,
    grossMinor: Int64 = 328_000,
    dailyFeeMinor: Int64? = 10_000,
    penaltyMinor: Int64? = nil,
    tipMinor: Int64? = nil,
    netMinor: Int64 = 318_000,
    trips: Int32 = 12
) -> EarningsSummary {
    EarningsSummary(
        period: period,
        rangeFrom: rangeFrom,
        rangeTo: rangeTo,
        grossMinor: grossMinor,
        dailyFeeMinor: dailyFeeMinor.map { KotlinLong(value: $0) },
        penaltyMinor: penaltyMinor.map { KotlinLong(value: $0) },
        tipMinor: tipMinor.map { KotlinLong(value: $0) },
        netMinor: netMinor,
        currency: Currency.lkr,
        trips: trips
    )
}

func sessionEarning(
    tripId: String = "01JTRIP00000000000000001",
    grossMinor: Int64 = 50_000,
    netMinor: Int64 = 48_000,
    endedAt: Date = testNow
) -> SessionEarning {
    SessionEarning(
        tripId: tripId,
        grossMinor: grossMinor,
        dailyFeeMinor: nil,
        penaltyMinor: nil,
        tipMinor: nil,
        netMinor: netMinor,
        currency: Currency.lkr,
        endedAt: timestamp(endedAt)
    )
}

// MARK: - Seams

/// ``JobsRepository`` with no gateway.
///
/// Every call is **recorded**, not only counted: the fence this cluster has to hold is that the only
/// write the board can produce is `postJobBoardIntent`, and a counter cannot say which operation ran.
final class FakeJobsRepository: JobsRepository {

    var standing = jobStanding()
    var boardRides: [ScheduledRide] = []
    var upcomingRides: [ScheduledRide] = []
    var nextBoardFailure: Error?
    var nextIntentFailure: Error?
    var nextUpcomingFailure: Error?
    var nextCancelFailure: Error?

    private(set) var standingReads: [String] = []
    private(set) var boardReads: [(lat: Double, lng: Double, radiusMetres: Int)] = []
    private(set) var intents: [String] = []
    private(set) var upcomingReads: [String] = []
    private(set) var cancellations: [String] = []

    func standing(driverId: String) async -> JobStanding {
        standingReads.append(driverId)
        return standing
    }

    func board(lat: Double, lng: Double, radiusMetres: Int) async throws -> [ScheduledRide] {
        boardReads.append((lat, lng, radiusMetres))
        try throwIf(&nextBoardFailure)
        return boardRides
    }

    func postIntent(scheduledRideId: String) async throws {
        intents.append(scheduledRideId)
        try throwIf(&nextIntentFailure)
    }

    func upcoming(driverId: String) async throws -> [ScheduledRide] {
        upcomingReads.append(driverId)
        try throwIf(&nextUpcomingFailure)
        return upcomingRides
    }

    func cancelScheduled(scheduledRideId: String) async throws {
        cancellations.append(scheduledRideId)
        try throwIf(&nextCancelFailure)
    }

    private func throwIf(_ failure: inout Error?) throws {
        guard let programmed = failure else { return }
        failure = nil
        throw programmed
    }
}

/// ``EarningsRepository`` with no gateway.
final class FakeEarningsRepository: EarningsRepository {

    var summaries: [EarningsPeriod: EarningsSummary] = [:]
    var sessions: [SessionEarning] = []
    var nextSummaryFailure: Error?
    var nextSessionsFailure: Error?

    private(set) var summaryReads: [EarningsPeriod] = []
    private(set) var sessionReads: [(from: BusinessDate, to: BusinessDate)] = []

    func summary(driverId: String, period: EarningsPeriod) async throws -> EarningsSummary {
        summaryReads.append(period)
        if let programmed = nextSummaryFailure {
            nextSummaryFailure = nil
            throw programmed
        }
        return summaries[period] ?? earningsSummary(period: period)
    }

    func sessions(driverId: String, from: BusinessDate, to: BusinessDate) async throws -> [SessionEarning] {
        sessionReads.append((from, to))
        if let programmed = nextSessionsFailure {
            nextSessionsFailure = nil
            throw programmed
        }
        return sessions
    }
}

/// A failure that is not a `MageRideError`, so it resolves to the shell's generic copy.
struct TestJobsFailure: Error {}
