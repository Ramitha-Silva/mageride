import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-019's arithmetic** — the ladder that stops at three, the server-tuned threshold, and the
/// two counters that are allowed to be absent.
@MainActor
final class DriverLevelModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var jobs: FakeJobsRepository!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        jobs = FakeJobsRepository()
    }

    private func makeModel() -> DriverLevelModel {
        DriverLevelModel(identity: identity, jobs: jobs)
    }

    // MARK: - The ladder

    func testTheBarFillsTowardTheNextLevel() async {
        jobs.standing = jobStanding(level: 2, points: 250)
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.level, 2)
        XCTAssertEqual(model.state.points, 250)
        XCTAssertEqual(model.state.threshold, 500, "D5' §4.2's own number, until an admin moves it")
        XCTAssertEqual(model.state.nextLevel, 3)
        XCTAssertEqual(model.state.progress, 0.5, accuracy: 0.001)
    }

    /// The wireframe draws *"510 / 500 pts → Level 4"*. D5' §4.2 caps at three, so there is no rung
    /// above and the copy says so — the recorded wireframe deviation, carried forward from C072.
    func testThereIsNoLevelFourAndTheBarIsFullAtTheTop() async {
        jobs.standing = jobStanding(level: 3, points: 510)
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.nextLevel, "min(level + 1, 3) leaves nothing to progress toward")
        XCTAssertTrue(model.state.isAtTopLevel)
        XCTAssertEqual(model.state.progress, 1, "a bar frozen at 2% would read as a driver who had stopped")
        XCTAssertEqual(Int(DriverLevelRules.companion.MAX_LEVEL), 3)
    }

    /// US-14.12 — `PUT /v1/admin/drivers/level-config` can move the threshold, so it is read and
    /// never baked.
    func testTheServersOwnThresholdWins() async {
        jobs.standing = jobStanding(level: 1, points: 150, levelUpThreshold: 300)
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.threshold, 300)
        XCTAssertEqual(model.state.progress, 0.5, accuracy: 0.001)
    }

    // MARK: - What is allowed to be absent

    func testAnUnreadLevelIsNotLevelThree() async {
        jobs.standing = unreadStanding()
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.level, "guessing the starting level would open the board to a demoted driver")
        XCTAssertEqual(model.state.progress, 0)
        XCTAssertFalse(model.state.isAtTopLevel)
    }

    func testAbsentCountersAreAbsentRatherThanZero() async {
        jobs.standing = jobStanding(acceptanceRate: nil, noShows: nil)
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.acceptancePercent, "an unread acceptance rate is not 0%")
        XCTAssertNil(model.state.noShows)
    }

    func testTheAcceptanceRateIsWholePercentAndIsClamped() async {
        jobs.standing = jobStanding(acceptanceRate: 0.925)
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.acceptancePercent, 92)

        jobs.standing = jobStanding(acceptanceRate: 1.4)
        await model.refresh()
        XCTAssertEqual(model.state.acceptancePercent, 100)
    }

    /// `GET …/level` answers `ratingPoints` and `GET …/stats` answers `points`; they are the same
    /// counter, and the stats read is the endpoint D3' files US-6A.14 under.
    func testTheStatsPointsWinOverTheLevelReadsRatingPoints() async {
        var standing = jobStanding(level: 2, points: 100)
        standing.points = 480
        jobs.standing = standing
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.points, 480)
    }
}
