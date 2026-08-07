import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-020's rules** — the total nobody recomputes, the window that comes from the server, and
/// the Colombo buckets underneath the chart.
@MainActor
final class EarningsModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var earnings: FakeEarningsRepository!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        earnings = FakeEarningsRepository()
    }

    private func makeModel(now: Date = testNow) -> EarningsModel {
        EarningsModel(identity: identity, earnings: earnings, now: { now })
    }

    // MARK: - The total is query-svc's

    /// The blunt version of the rule: rows that sum to Rs 1 under a card that still says Rs 3,180.
    func testTheSummaryIsPrintedAsSentAndIsNeverReSummedFromTheRows() async {
        earnings.summaries[EarningsPeriod.today] = earningsSummary(netMinor: 318_000, grossMinor: 328_000)
        earnings.sessions = [sessionEarning(grossMinor: 100, netMinor: 100)]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.netMinor, 318_000, "R-05 means an in-flight payment is on neither")
        XCTAssertEqual(model.state.grossMinor, 328_000)
        XCTAssertEqual(model.state.tripCount, 12, "the card's trip count is the summary's, not the page's")
        XCTAssertEqual(model.state.trips.count, 1)
    }

    func testAnAbsentOptionalFigureIsZeroRatherThanMissing() async {
        earnings.summaries[EarningsPeriod.today] = earningsSummary(
            dailyFeeMinor: nil,
            penaltyMinor: nil,
            tipMinor: nil
        )
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.dailyFeeMinor, 0, "a driver who has never been penalised gets no penalty line")
        XCTAssertEqual(model.state.penaltyMinor, 0)
        XCTAssertEqual(model.state.tipMinor, 0)
    }

    func testAnAnsweredPeriodWithNoTripsIsTheEmptyPeriod() async {
        earnings.summaries[EarningsPeriod.today] = earningsSummary(netMinor: 0, grossMinor: 0, trips: 0)
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.isEmptyPeriod)
    }

    // MARK: - The window, and where it comes from

    func testTheSessionsReadUsesTheSummarysOwnWindow() async {
        let from = IosBusinessDateKt.colomboBusinessDateNow()
        earnings.summaries[EarningsPeriod.month] = earningsSummary(
            period: EarningsPeriod.month,
            rangeFrom: from,
            rangeTo: from
        )
        let model = makeModel()

        await model.select(EarningsPeriod.month)

        XCTAssertEqual(earnings.sessionReads.count, 1)
        XCTAssertEqual(earnings.sessionReads.first?.from, from, "the rows describe the days the card counted")
        XCTAssertEqual(earnings.sessionReads.first?.to, from)
    }

    /// `query.yaml` marks the range optional, so a response without one is legal — and the fallback
    /// is query-svc's own window rather than the handset's idea of a week.
    func testAWeekWithNoRangeFallsBackToTheSevenDaysEndingToday() async {
        earnings.summaries[EarningsPeriod.week] = earningsSummary(period: EarningsPeriod.week)
        let model = makeModel()

        await model.select(EarningsPeriod.week)

        let today = IosBusinessDateKt.colomboBusinessDateNow()
        XCTAssertEqual(earnings.sessionReads.first?.to, today)
        XCTAssertEqual(
            earnings.sessionReads.first?.from,
            BusinessCalendar.shared.plusDays(date: today, days: -6)
        )
    }

    func testAMonthWithNoRangeFallsBackToTheCalendarMonthToDate() async {
        earnings.summaries[EarningsPeriod.month] = earningsSummary(period: EarningsPeriod.month)
        let model = makeModel()

        await model.select(EarningsPeriod.month)

        let today = IosBusinessDateKt.colomboBusinessDateNow()
        XCTAssertEqual(earnings.sessionReads.first?.from, BusinessCalendar.shared.firstOfMonth(date: today))
    }

    func testEachTabReReadsBothEndpoints() async {
        let model = makeModel()

        await model.select(EarningsPeriod.week)
        await model.select(EarningsPeriod.month)

        XCTAssertEqual(earnings.summaryReads, [EarningsPeriod.week, EarningsPeriod.month])
        XCTAssertEqual(earnings.sessionReads.count, 2)
        XCTAssertEqual(model.state.period, EarningsPeriod.month)
    }

    // MARK: - The rows

    func testTripsAreNewestFirst() async {
        earnings.sessions = [
            sessionEarning(tripId: "early", endedAt: testNow.addingTimeInterval(-4 * 3600)),
            sessionEarning(tripId: "latest", endedAt: testNow),
            sessionEarning(tripId: "middle", endedAt: testNow.addingTimeInterval(-2 * 3600)),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.trips.map(\.tripId), ["latest", "middle", "early"])
    }

    func testAFailedReadBecomesCopy() async {
        earnings.nextSummaryFailure = TestJobsFailure()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)

        model.dismissError()
        XCTAssertNil(model.state.errorKey)
    }

    // MARK: - The chart

    func testTodayIsBucketedByHourAndHighlightsTheHourTheClockIsIn() async {
        earnings.sessions = [
            sessionEarning(tripId: "a", netMinor: 5_000, endedAt: testNow.addingTimeInterval(-2 * 3600)),
            sessionEarning(tripId: "b", netMinor: 7_000, endedAt: testNow),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.buckets.count, 3, "12:00, an idle 13:00, and 14:00")
        XCTAssertEqual(model.state.buckets.map(\.netMinor), [5_000, 0, 7_000])
        XCTAssertEqual(model.state.buckets.last?.isCurrent, true)
        XCTAssertEqual(model.state.buckets.filter(\.isCurrent).count, 1)
    }

    func testAnIdleAfternoonIsEmptyBarsRatherThanADayThatEndedAtLunchtime() async {
        earnings.sessions = [sessionEarning(netMinor: 5_000, endedAt: testNow.addingTimeInterval(-3 * 3600))]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.buckets.count, 4)
        XCTAssertEqual(model.state.buckets.dropFirst().allSatisfy { $0.netMinor == 0 }, true)
    }
}
