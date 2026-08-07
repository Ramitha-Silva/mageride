import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-017's rules** — the four that live in ``JobBoardModel`` and nowhere else.
///
/// US-6A.8's three-valued gate, D5' §3.7's T-30 window, the post-intent fence, and the order the
/// board is drawn in.
@MainActor
final class JobBoardModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var jobs: FakeJobsRepository!
    private var location: FakeDriverLocationSource!
    private var clock: Date!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        jobs = FakeJobsRepository()
        location = FakeDriverLocationSource()
        clock = testNow
    }

    /// - Parameter positionWait: zero by default, because every test that expects rows seeds the fix
    ///   first and the one that does not is asserting the refusal — neither has any reason to spend
    ///   eight real seconds looking for a GNSS receiver a test host does not have.
    private func makeModel(positionWait: TimeInterval = 0) -> JobBoardModel {
        JobBoardModel(
            identity: identity,
            jobs: jobs,
            location: location,
            now: { self.clock },
            positionWait: positionWait
        )
    }

    /// The board only reads once a fix exists; every test that expects rows starts from one.
    private func makeModelWithFix() -> JobBoardModel {
        location.emit(testFix())
        return makeModel()
    }

    // MARK: - US-6A.8, and the third value

    func testLevelOneSeesTheGateAndNoBoardReadIsMade() async {
        jobs.standing = jobStanding(level: 1)
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertEqual(model.state.isGated, true)
        XCTAssertEqual(model.state.minimumLevel, 2, "the board opens at Level 2 (US-6A.8)")
        XCTAssertTrue(model.state.rows.isEmpty)
        XCTAssertTrue(
            jobs.boardReads.isEmpty,
            "a gated driver's board read is a round trip to draw a list of disabled buttons"
        )
    }

    /// The single failure US-6A.8 must never produce.
    func testAnUnreadLevelIsUnavailableAndIsNeverTheGate() async {
        jobs.standing = unreadStanding()
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertNil(model.state.isGated, "no answer is not the same as `you are Level 1`")
        XCTAssertTrue(model.state.isUnavailable)
        XCTAssertFalse(model.state.isEmpty, "an unread level is not an empty city either")
    }

    func testAnAnsweredUngatedEmptyBoardIsTheEmptyState() async {
        jobs.boardRides = []
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertEqual(model.state.isGated, false)
        XCTAssertTrue(model.state.isEmpty)
        XCTAssertFalse(model.state.isUnavailable)
    }

    // MARK: - The catchment (D-06)

    func testTheBoardIsReadAtTheDriversOwnPositionAndTheD06Radius() async {
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertEqual(jobs.boardReads.count, 1)
        XCTAssertEqual(jobs.boardReads.first?.lat, testHere.lat)
        XCTAssertEqual(jobs.boardReads.first?.lng, testHere.lng)
        XCTAssertEqual(
            jobs.boardReads.first?.radiusMetres,
            Int(JobBoard.companion.CATCHMENT_METRES),
            "the number the screen prints and the number it asks for are one constant"
        )
    }

    /// A `(0, 0)` the server would answer honestly and uselessly is never sent.
    func testNoFixMeansNoBoardReadAndCopyTheDriverCanActOn() async {
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(jobs.boardReads.isEmpty)
        XCTAssertEqual(model.state.errorKey, "job_board_no_position")
        XCTAssertEqual(model.state.isGated, false, "the gate is not what is wrong here")
    }

    /// The wait is **bounded** rather than absent: a fix that arrives while the read is in flight is
    /// the ordinary case on a handset that has just been unlocked, and the board takes it.
    func testAFixThatArrivesDuringTheWaitIsUsed() async {
        let model = makeModel(positionWait: JobBoardModel.positionWaitSeconds)
        let refreshing = Task { await model.refresh() }

        location.emit(testFix())
        await refreshing.value

        XCTAssertEqual(jobs.boardReads.count, 1)
        XCTAssertNil(model.state.errorKey)
    }

    // MARK: - D5' §3.7, the T-30 window

    func testRowsAreOrderedBySoonestPickup() async {
        jobs.boardRides = [
            scheduledRide(id: "late", pickupIn: 6 * 3600),
            scheduledRide(id: "soon", pickupIn: 2 * 3600),
            scheduledRide(id: "middle", pickupIn: 4 * 3600),
        ]
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertEqual(model.state.rows.map(\.id), ["soon", "middle", "late"])
    }

    /// The fade is for a card that dies on screen, not for one that was dead when it landed.
    func testARideAlreadyPastItsWindowIsNeverDrawn() async {
        jobs.boardRides = [scheduledRide(id: "gone", pickupIn: 5 * 60)]
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertTrue(model.state.rows.isEmpty, "T-30 passed ten minutes before this read landed")
    }

    func testARowInsideTheWindowIsNotExpiredAndCanPost() async {
        jobs.boardRides = [scheduledRide(pickupIn: 90 * 60)]
        let model = makeModelWithFix()

        await model.refresh()

        guard let row = model.state.rows.first else { return XCTFail("the board dropped a live row") }
        XCTAssertFalse(row.isExpired)
        XCTAssertTrue(row.canPost)
    }

    /// A pickup 31 minutes out goes live in one minute, so the same row is on both sides of T-30
    /// inside a single test — which is exactly why the clock is injected.
    func testARowGoesExpiredTheSecondItsWindowCloses() async {
        jobs.boardRides = [scheduledRide(pickupIn: 31 * 60)]
        let model = makeModelWithFix()
        await model.refresh()
        XCTAssertEqual(model.state.rows.first?.isExpired, false)

        clock = testNow.addingTimeInterval(61)
        await model.refresh()

        guard let row = model.state.rows.first else { return XCTFail("the fade window had not passed yet") }
        XCTAssertTrue(row.isExpired, "past T-30 the board no longer takes intent")
        XCTAssertFalse(row.canPost)
    }

    /// The card fades and *then* leaves — the DoD's "rows disappear once their T-30 window passes"
    /// and D2' §SCR-DI-017's "card expire fade" are one behaviour rather than two.
    func testAnExpiredRowLeavesTheListOnceItsFadeHasBeenSeen() async {
        jobs.boardRides = [scheduledRide(pickupIn: 31 * 60)]
        let model = makeModelWithFix()
        await model.refresh()

        clock = testNow.addingTimeInterval(61)
        await model.refresh()
        XCTAssertEqual(model.state.rows.count, 1, "still up, and faded")

        clock = testNow.addingTimeInterval(60 + JobBoardModel.expiryFadeSeconds + 1)
        await model.refresh()
        XCTAssertTrue(model.state.rows.isEmpty)
    }

    // MARK: - US-6A.5, and the fence

    func testPostingIntentSendsExactlyOneIntentAndNothingElse() async {
        jobs.boardRides = [scheduledRide(pickupIn: 90 * 60)]
        let model = makeModelWithFix()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("no row to post on") }

        await model.postIntent(row)

        XCTAssertEqual(jobs.intents, [testScheduledRideId])
        XCTAssertEqual(jobs.cancellations, [], "the board has no accept and no cancel — R-01's fence")
    }

    /// The pill is backed by a set held in the model, because nothing on the wire carries the fact.
    func testAPostedRowShowsTheIntentPostedPillAndCannotPostAgain() async {
        jobs.boardRides = [scheduledRide(pickupIn: 90 * 60)]
        let model = makeModelWithFix()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("no row to post on") }

        await model.postIntent(row)

        guard let updated = model.state.rows.first else { return XCTFail("the row left the board") }
        XCTAssertTrue(updated.isPosted)
        XCTAssertFalse(updated.canPost)

        await model.postIntent(updated)
        XCTAssertEqual(jobs.intents.count, 1, "a second tap on a posted row sends nothing")
    }

    func testAFailedIntentClearsTheSpinnerAndBecomesCopy() async {
        jobs.boardRides = [scheduledRide(pickupIn: 90 * 60)]
        jobs.nextIntentFailure = TestJobsFailure()
        let model = makeModelWithFix()
        await model.refresh()
        guard let row = model.state.rows.first else { return XCTFail("no row to post on") }

        await model.postIntent(row)

        XCTAssertEqual(model.state.rows.first?.isPosting, false)
        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.rows.first?.isPosted, false, "nothing was posted, so nothing says it was")
    }

    func testAFailedBoardReadBecomesCopyAndLeavesTheGateAnswered() async {
        jobs.nextBoardFailure = TestJobsFailure()
        let model = makeModelWithFix()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.isGated, false)
        XCTAssertFalse(model.state.isLoading)
    }

    func testDismissingTheErrorClearsIt() async {
        jobs.nextBoardFailure = TestJobsFailure()
        let model = makeModelWithFix()
        await model.refresh()

        model.dismissError()

        XCTAssertNil(model.state.errorKey)
    }

    // MARK: - The GNSS subscription

    func testTheBoardDropsItsPositionSubscriptionWhenItLeaves() {
        let model = makeModel()

        model.start()
        model.stop()

        XCTAssertEqual(location.startCount, 1)
        XCTAssertEqual(location.stopCount, 1, "a screen that is not visible must not hold a GNSS one open")
    }
}
