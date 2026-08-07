import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-018's rules** — US-6A.15's reminder instant, the countdown, and what a driver may and
/// may not withdraw.
@MainActor
final class ScheduledRidesModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var jobs: FakeJobsRepository!
    private var clock: Date!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        jobs = FakeJobsRepository()
        clock = testNow
    }

    private func makeModel() -> ScheduledRidesModel {
        ScheduledRidesModel(identity: identity, jobs: jobs, now: { self.clock })
    }

    // MARK: - The list

    func testRowsAreOrderedBySoonestPickupWhateverTheServerSent() async {
        jobs.upcomingRides = [
            scheduledRide(id: "late", pickupIn: 5 * 3600),
            scheduledRide(id: "soon", pickupIn: 20 * 60),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.rows.map(\.id), ["soon", "late"])
    }

    func testACancelledBookingIsNotOnTheList() async {
        jobs.upcomingRides = [
            scheduledRide(id: "gone", pickupIn: 3600, status: ScheduledRideStatus.cancelled),
            scheduledRide(id: "live", pickupIn: 2 * 3600),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.rows.map(\.id), ["live"])
    }

    func testNothingUpcomingIsTheEmptyState() async {
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.isEmpty)
        XCTAssertFalse(model.state.isLoading)
    }

    // MARK: - US-6A.15, and the instant it shares with the board

    /// The reminder and the Job Board's go-live are the **same** T-30, so neither screen keeps a
    /// threshold of its own. A pickup 28 minutes away is inside it; one 40 minutes away is not.
    func testTheReminderIsDueExactlyWhenTheBoardWouldHaveExpiredTheSameRide() async {
        jobs.upcomingRides = [
            scheduledRide(id: "imminent", pickupIn: 28 * 60),
            scheduledRide(id: "later", pickupIn: 40 * 60),
        ]
        let model = makeModel()

        await model.refresh()

        let rows = Dictionary(uniqueKeysWithValues: model.state.rows.map { ($0.id, $0) })
        XCTAssertEqual(rows["imminent"]?.hasReminderFired, true)
        XCTAssertEqual(rows["later"]?.hasReminderFired, false)
    }

    func testTheCountdownIsTheMinutesLeftAndNeverGoesNegative() async {
        jobs.upcomingRides = [scheduledRide(pickupIn: 28 * 60 + 30)]
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.rows.first?.minutesToPickup, 28)

        clock = testNow.addingTimeInterval(60 * 60)
        await model.refresh()

        XCTAssertEqual(model.state.rows.first?.secondsToPickup, 0, "a pickup in the past is zero, not a negative")
        XCTAssertEqual(model.state.rows.first?.minutesToPickup, 0)
    }

    // MARK: - Cancellation (the C072 spec gap, carried forward)

    func testAScheduledBookingCanBeGivenUpAndLeavesTheList() async {
        jobs.upcomingRides = [scheduledRide(pickupIn: 5 * 3600)]
        let model = makeModel()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("nothing to give up") }

        await model.cancel(row)

        XCTAssertEqual(jobs.cancellations, [testScheduledRideId])
        XCTAssertTrue(model.state.rows.isEmpty)
    }

    /// From T-30 the ride exists and ride-svc's cancel — with its penalty matrix — is the only door.
    func testADispatchedRideIsNeverSentToTheScheduleCancelRoute() async {
        jobs.upcomingRides = [scheduledRide(pickupIn: 10 * 60, status: ScheduledRideStatus.dispatched)]
        let model = makeModel()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("the dispatched row was dropped") }

        XCTAssertFalse(row.isScheduled, "the button is dead on a dispatched row")
        await model.cancel(row)

        XCTAssertTrue(jobs.cancellations.isEmpty)
        XCTAssertEqual(model.state.rows.count, 1, "and the booking stays on the list")
    }

    /// The route is the passenger's, so a driver's call is a `403`. The refusal is rendered as copy
    /// and the booking is left where it was — see ``ScheduledRidesModel``.
    func testARefusedCancellationBecomesCopyAndKeepsTheBooking() async {
        jobs.upcomingRides = [scheduledRide(pickupIn: 5 * 3600)]
        jobs.nextCancelFailure = TestJobsFailure()
        let model = makeModel()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("nothing to give up") }

        await model.cancel(row)

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.rows.count, 1)
        XCTAssertEqual(model.state.rows.first?.isCancelling, false)
    }

    func testAFailedReadBecomesCopy() async {
        jobs.nextUpcomingFailure = TestJobsFailure()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)

        model.dismissError()
        XCTAssertNil(model.state.errorKey)
    }
}
