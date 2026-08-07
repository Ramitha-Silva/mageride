import MageRideShared
import XCTest

@testable import DriverApp

/// **D-38, on the two screens that print a clock and the one that buckets a chart.**
///
/// Every assertion here is written against ``testMidnightEdge`` — 19:00 UTC, which is already the
/// **next** day in Colombo. A label or a bucket computed in UTC, or in whatever zone the machine
/// running these tests happens to be in, lands on the wrong day at exactly that instant; one
/// computed in Asia/Colombo does not. That is the same fixture
/// `apps/driver-android/.../jobs/ScheduleLabelsTest.kt` uses, for the same reason.
final class ScheduleLabelTests: XCTestCase {

    // MARK: - The zone

    func testTheZoneIsSharedsAndNotSpelledTwice() {
        XCTAssertEqual(ScheduleLabels.zone.identifier, "Asia/Colombo")
        XCTAssertEqual(ScheduleLabels.zone.identifier, IosBusinessDateKt.colomboZoneId())
        XCTAssertEqual(ScheduleLabels.calendar.timeZone, ScheduleLabels.zone)
        XCTAssertEqual(
            ScheduleLabels.calendar.identifier,
            .gregorian,
            "a non-Gregorian handset calendar would answer a different day of the month"
        )
    }

    // MARK: - The clock

    /// Fixed 24-hour, whatever the handset's region and whatever its 12/24-hour switch says.
    func testTheTimeIsColomboWallClockInTwentyFourHours() {
        XCTAssertEqual(ScheduleLabels.time(timestamp(testNow)), "14:00")
        XCTAssertEqual(ScheduleLabels.time(timestamp(testMidnightEdge)), "00:30", "19:00 UTC is 00:30 in Colombo")
    }

    // MARK: - The calendar

    func testTodayAndTomorrowAreColomboDaysRatherThanDurations() {
        // Nine hours after 14:00 is 23:00 the same Colombo day.
        XCTAssertEqual(
            ScheduleLabels.day(timestamp(testNow.addingTimeInterval(9 * 3600)), now: timestamp(testNow)),
            .today
        )
        // Nine hours after 22:00 is 07:00 the next one — the same nine hours, a different answer.
        let lateEvening = testNow.addingTimeInterval(8 * 3600)
        XCTAssertEqual(
            ScheduleLabels.day(timestamp(lateEvening.addingTimeInterval(9 * 3600)), now: timestamp(lateEvening)),
            .tomorrow
        )
    }

    /// The instant a UTC calendar gets wrong. `now` is 19:00 UTC on the 17th, which is already
    /// **00:30 on the 18th** in Colombo; the pickup is 20:00 Colombo on that same 18th. Read in
    /// Colombo both fall on the 18th and the card says *"Today"*; read in UTC they are the 17th and
    /// the 18th, and the card would say *"Tomorrow"* on a job the driver has to leave for tonight.
    func testTheMidnightEdgeIsReadInColombo() {
        let pickup = testMidnightEdge.addingTimeInterval(19.5 * 3600)

        XCTAssertEqual(ScheduleLabels.day(timestamp(pickup), now: timestamp(testMidnightEdge)), .today)
        XCTAssertEqual(ScheduleLabels.time(timestamp(pickup)), "20:00")
    }

    func testAnyOtherDayIsARenderedDateRatherThanCopy() {
        let label = ScheduleLabels.day(timestamp(testNow.addingTimeInterval(6 * 86_400)), now: timestamp(testNow))

        guard case .on(let text) = label else { return XCTFail("a week out is neither today nor tomorrow") }
        XCTAssertFalse(text.isEmpty)
        XCTAssertTrue(
            text.contains("23"),
            "the 17th plus six days is the 23rd in Colombo, whatever the month name reads as"
        )
    }

    // MARK: - The route line

    func testTheRouteIsPickupArrowDrop() {
        let ride = scheduledRide(pickupIn: 3600)

        XCTAssertEqual(
            ScheduleLabels.route(pickup: ride.pickup, dropoff: ride.dropoff),
            "Maharagama" + MageRideSymbols.routeArrow + "Fort"
        )
    }

    /// `POST /v1/rides/schedule` takes bare coordinates, so **every** scheduled ride comes back with
    /// no address at all. A dash is what a card shows rather than a pair of decimal degrees.
    func testAnAbsentAddressIsADashAndNeverACoordinate() {
        let ride = scheduledRide(pickupIn: 3600, pickupAddress: nil, dropoffAddress: nil)
        let line = ScheduleLabels.route(pickup: ride.pickup, dropoff: ride.dropoff)

        XCTAssertEqual(line, MageRideSymbols.unknown + MageRideSymbols.routeArrow + MageRideSymbols.unknown)
        XCTAssertFalse(line.contains("6.9"), "no decimal degrees ever reach a card")
    }
}
