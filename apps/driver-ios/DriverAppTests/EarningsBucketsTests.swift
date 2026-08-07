import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-020's chart, and the tab strip above it.**
///
/// The buckets are Asia/Colombo (D-13, D-38) because `?period=` is evaluated there by query-svc — a
/// chart bucketed in the handset's zone draws the server's day across two of its own bars. Every day
/// assertion here is written against ``testMidnightEdge``, which is already the next Colombo day.
final class EarningsBucketsTests: XCTestCase {

    // MARK: - Today, by hour

    func testTodayIsOneBarPerHourFromTheFirstTripThroughNow() {
        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.today,
            sessions: [
                sessionEarning(tripId: "a", netMinor: 3_000, endedAt: testNow.addingTimeInterval(-3 * 3600)),
                sessionEarning(tripId: "b", netMinor: 4_000, endedAt: testNow.addingTimeInterval(-3 * 3600)),
                sessionEarning(tripId: "c", netMinor: 9_000, endedAt: testNow),
            ],
            from: IosBusinessDateKt.colomboBusinessDateNow(),
            to: IosBusinessDateKt.colomboBusinessDateNow(),
            now: testNow
        )

        XCTAssertEqual(buckets.map(\.label), ["11", "12", "13", "14"], "14:00 Colombo, back to the first trip")
        XCTAssertEqual(buckets.map(\.netMinor), [7_000, 0, 0, 9_000], "two trips in one hour are one bar")
        XCTAssertEqual(buckets.map(\.isCurrent), [false, false, false, true])
    }

    /// A shift with nothing on it is still the hour the clock is in, not an empty chart.
    func testAnEmptyDayIsTheCurrentHourAlone() {
        let today = IosBusinessDateKt.colomboBusinessDateNow()
        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.today,
            sessions: [],
            from: today,
            to: today,
            now: testNow
        )

        XCTAssertEqual(buckets.map(\.label), ["14"])
        XCTAssertEqual(buckets.first?.netMinor, 0)
    }

    /// 19:00 UTC is 00:30 in Colombo, so the trip belongs to hour `00` of the **next** day — the
    /// bucket a UTC or handset-zone chart would file under 19.
    func testTheHourIsReadInColombo() {
        let today = IosBusinessDateKt.colomboBusinessDateNow()
        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.today,
            sessions: [sessionEarning(netMinor: 1_000, endedAt: testMidnightEdge)],
            from: today,
            to: today,
            now: testMidnightEdge
        )

        XCTAssertEqual(buckets.map(\.label), ["00"])
        XCTAssertEqual(buckets.first?.netMinor, 1_000)
    }

    // MARK: - A week or a month, by Colombo day

    func testAWindowIsOneBarPerColomboDayIncludingTheQuietOnes() {
        let calendar = ScheduleLabels.calendar
        let start = calendar.startOfDay(for: testNow.addingTimeInterval(-2 * 86_400))

        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.week,
            sessions: [
                sessionEarning(tripId: "a", netMinor: 2_000, endedAt: start.addingTimeInterval(10 * 3600)),
                sessionEarning(tripId: "b", netMinor: 6_000, endedAt: testNow),
            ],
            from: businessDate(daysBeforeNow: 2),
            to: businessDate(daysBeforeNow: 0),
            now: testNow
        )

        XCTAssertEqual(buckets.map(\.label), ["15", "16", "17"], "the 15th, a quiet 16th, and today")
        XCTAssertEqual(buckets.map(\.netMinor), [2_000, 0, 6_000])
        XCTAssertEqual(buckets.map(\.isCurrent), [false, false, true])
    }

    /// A trip at 19:00 UTC belongs to the **18th** in Colombo, not the 17th the summary's window ends
    /// on — which is the whole reason `BusinessCalendar` exists.
    func testATripOnTheMidnightEdgeCountsAgainstTheColomboDay() {
        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.month,
            sessions: [sessionEarning(netMinor: 4_000, endedAt: testMidnightEdge)],
            from: businessDate(daysBeforeNow: 1),
            to: businessDate(daysBeforeNow: -1),
            now: testNow
        )

        XCTAssertEqual(buckets.map(\.label), ["16", "17", "18"])
        XCTAssertEqual(buckets.map(\.netMinor), [0, 0, 4_000], "the 18th, not the 17th")
    }

    func testAWindowThatEndsBeforeItBeginsIsNoChart() {
        let buckets = EarningsBuckets.of(
            period: EarningsPeriod.week,
            sessions: [],
            from: businessDate(daysBeforeNow: 0),
            to: businessDate(daysBeforeNow: 3),
            now: testNow
        )

        XCTAssertTrue(buckets.isEmpty)
    }

    // MARK: - The tab strip

    /// `EarningsPeriod.entries` does not cross the bridge, so the order is written out in Swift. This
    /// is what stops that table and the shared enum drifting: a fourth window added to `query.yaml`
    /// fails here rather than quietly losing a tab.
    func testThePeriodTableIsTheSharedEnumsOwnOrder() {
        XCTAssertEqual(EarningsPeriods.all.map(\.ordinal), [0, 1, 2])
        XCTAssertEqual(EarningsPeriods.all.map(\.wire), ["today", "week", "month"], "the contract's own spellings")
        XCTAssertEqual(Set(EarningsPeriods.all.map(\.labelKey)).count, 3, "three tabs, three distinct labels")
    }

    func testEveryKeyTheseTwoScreensRenderHasAnEntry() {
        let keys = EarningsPeriods.all.map(\.labelKey) + [
            "earnings_title",
            "earnings_net_for",
            "earnings_fares_received",
            "earnings_tips",
            "earnings_daily_fee",
            "earnings_penalties",
            "earnings_net",
            "earnings_trips",
            "earnings_empty",
            "earnings_chart_bucket",
            "earnings_chart_label",
        ]

        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no entry in Localizable.strings")
        }
    }

    // MARK: -

    /// The Colombo business date [days] before ``testNow`` — negative counts forward.
    ///
    /// Through `:shared` rather than a Foundation `DateComponents`, so the window the test builds is
    /// the same kind of value the server's `rangeFrom`/`rangeTo` are and is resolved in the same zone.
    private func businessDate(daysBeforeNow days: Int) -> BusinessDate {
        let at = testNow.addingTimeInterval(TimeInterval(-days) * 86_400)
        return IosBusinessDateKt.colomboBusinessDateOf(at: timestamp(at))
    }
}
