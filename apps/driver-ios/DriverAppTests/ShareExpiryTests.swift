import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-028's **Expiry** — a grant lapses at the **end** of the chosen Colombo day (US-4.8, D-38).
///
/// The Android twin's file is mostly a warning about M3's date picker answering UTC midnight; here the
/// picker is handed ``ScheduleLabels/calendar`` and ``ScheduleLabels/zone``, so what is left to get
/// wrong is the *end* of the day — and sending Colombo **midnight** would revoke the passenger a whole
/// day early, which is precisely what US-4.8's auto-revoke would then act on.
final class ShareExpiryTests: XCTestCase {

    /// 09:00 on 30 June 2026 in Colombo (03:30 UTC) — a moment inside the day, not its boundary, and
    /// deliberately one whose UTC date and Colombo date are the same so a failure means the *end* of
    /// the day was got wrong rather than the day itself.
    private let middleOfTheDay = Date(timeIntervalSince1970: 1_782_790_200)

    func testAnExpiryIsTheLastInstantOfTheChosenColomboDay() {
        let lapses = ScheduleLabels.instant(ShareExpiry.endOfDay(middleOfTheDay))
        let parts = ScheduleLabels.calendar.dateComponents(
            [.year, .month, .day, .hour, .minute, .second],
            from: lapses
        )

        XCTAssertEqual(parts.year, 2026)
        XCTAssertEqual(parts.month, 6)
        XCTAssertEqual(parts.day, 30, "the day the driver tapped, in Colombo")
        XCTAssertEqual(parts.hour, 23)
        XCTAssertEqual(parts.minute, 59)
        XCTAssertEqual(parts.second, 59)
    }

    /// Every instant inside one Colombo day answers the same expiry: the *day* is the choice, and the
    /// hour the picker happened to carry is not part of it.
    func testEveryInstantInsideOneColomboDayAnswersTheSameExpiry() {
        let startOfDay = ScheduleLabels.calendar.startOfDay(for: middleOfTheDay)
        let lateEvening = startOfDay.addingTimeInterval(23 * 3_600 + 59 * 60)

        XCTAssertEqual(
            ScheduleLabels.instant(ShareExpiry.endOfDay(startOfDay)),
            ScheduleLabels.instant(ShareExpiry.endOfDay(lateEvening))
        )
    }

    /// The expiry is **after** every moment of the chosen day and **before** the next one begins —
    /// which is the whole property US-4.8 acts on.
    func testTheExpiryFallsAfterTheChosenDayAndBeforeTheNext() {
        let calendar = ScheduleLabels.calendar
        let startOfDay = calendar.startOfDay(for: middleOfTheDay)
        let nextDay = calendar.date(byAdding: DateComponents(day: 1), to: startOfDay)!
        let lapses = ScheduleLabels.instant(ShareExpiry.endOfDay(middleOfTheDay))

        XCTAssertGreaterThan(lapses, startOfDay)
        XCTAssertLessThan(lapses, nextDay)
    }

    /// Re-opening the picker shows the day in force rather than today — a driver adjusting a date they
    /// set last week should not have to find it again.
    func testAStoredExpiryReadsBackAsTheDayItWasSetFor() {
        let stored = ShareExpiry.endOfDay(middleOfTheDay)
        let reopened = ShareExpiry.date(stored)

        XCTAssertEqual(reopened, ScheduleLabels.calendar.startOfDay(for: middleOfTheDay))
    }

    /// The year is carried where ``ScheduleLabels/date(_:)`` drops it: a grant can be set to lapse in a
    /// year's time and *"30 Jun"* would then be genuinely ambiguous.
    func testTheLabelCarriesTheYear() {
        let label = ShareExpiry.label(ShareExpiry.endOfDay(middleOfTheDay))

        XCTAssertTrue(label.contains("2026"), "an expiry names its year: \(label)")
    }
}
