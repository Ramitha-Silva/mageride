import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-010 / SCR-DI-011's rules** — the four that live in ``HomeModel`` and nowhere else.
///
/// Every one of them is a rule the wireframe states and a screen cannot be trusted to re-derive:
/// US-9.6's gate, the order the two go-online calls are made in, DT-04's local mirror, and AL-32's
/// "the dashboard outranks the tracker".
@MainActor
final class HomeModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var standby: FakeStandbyRepository!
    private var journeys: FakeJourneyRepository!
    private var rides: FakeActiveRideRepository!
    private var location: FakeDriverLocationSource!
    private var publisher: FakePositionPublisher!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        standby = FakeStandbyRepository()
        journeys = FakeJourneyRepository()
        rides = FakeActiveRideRepository()
        location = FakeDriverLocationSource()
        publisher = FakePositionPublisher()
    }

    private func makeModel() -> HomeModel {
        HomeModel(
            identity: identity,
            standby: standby,
            journeys: journeys,
            rides: rides,
            location: location,
            publisher: publisher
        )
    }

    private func approvedTuk(mode: ServiceMode = ServiceMode.c) -> VehicleSummary {
        summary(
            vehicleType: VehicleType.threeWheeler,
            mode: mode,
            status: RegistrationStatus.approved,
            onboardingStatus: OnboardingStatus.approved
        )
    }

    // MARK: - US-9.6, the go-online gate

    func testTheToggleIsDeadWithNoEligibleVehicle() async {
        identity.live = LiveVehicle(vehicles: [summary()], live: nil)
        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.canGoOnline)
        XCTAssertTrue(model.state.needsVehicle, "US-9.6's empty state routes to SCR-DI-026a")

        await model.toggleOnline(true)
        XCTAssertEqual(model.state.errorKey, "home_needs_vehicle")
        XCTAssertTrue(standby.goneOnline.isEmpty, "nothing may be sent for a driver with no vehicle")
        XCTAssertTrue(publisher.events.isEmpty)
    }

    /// A read still in flight is not the same as no vehicle: the copy under the toggle must not appear
    /// before the answer is in.
    func testTheEmptyStateWaitsForTheFirstReadRatherThanShowingWhileItLoads() {
        let model = makeModel()
        XCTAssertTrue(model.state.isLoading)
        XCTAssertFalse(model.state.needsVehicle)
        XCTAssertFalse(model.state.canGoOnline)
    }

    func testGoingOnlineIsRefusedUntilTheFirstFixArrives() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        let model = makeModel()
        await model.refresh()

        await model.toggleOnline(true)
        XCTAssertEqual(model.state.errorKey, "home_waiting_for_gps")
        XCTAssertTrue(standby.goneOnline.isEmpty, "a GoOnlineRequest with a made-up point is a lie")
    }

    // MARK: - Going online is two calls in one order

    func testGoingOnlinePublishesOnlyAfterTheCallIsAccepted() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        let model = makeModel()
        model.start()
        await model.refresh()
        location.emit(testFix())

        await model.toggleOnline(true)

        XCTAssertEqual(standby.goneOnline.count, 1)
        XCTAssertEqual(standby.goneOnline.first?.vehicleId, testVehicleId)
        XCTAssertEqual(standby.goneOnline.first?.position.lat, testHere.lat)
        XCTAssertEqual(publisher.events, ["start:" + testVehicleId])
        XCTAssertTrue(model.state.isOnline)
    }

    func testGoingOfflineStopsPublishingBeforeTheCallIsMade() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        standby.standing = DriverStanding(directional: directionalFilter(active: true, secondsLeft: 3_600))
        let model = makeModel()
        model.start()
        await model.refresh()
        location.emit(testFix())
        await model.toggleOnline(true)
        XCTAssertEqual(publisher.events, ["start:" + testVehicleId])

        await model.toggleOnline(false)

        XCTAssertEqual(publisher.events, ["start:" + testVehicleId, "stop"])
        XCTAssertEqual(standby.goneOfflineCount, 1)
        XCTAssertFalse(model.state.isOnline)
    }

    /// **DT-04** — going offline clears the filter, and the use is **not** refunded.
    func testGoingOfflineClearsTheDirectionalFilterButNotTheDayReserves() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        standby.standing = DriverStanding(
            directional: directionalFilter(active: true, usesRemaining: 1, secondsLeft: 3_600, label: "Nugegoda")
        )
        let model = makeModel()
        model.start()
        await model.refresh()
        location.emit(testFix())
        await model.toggleOnline(true)

        await model.toggleOnline(false)

        XCTAssertEqual(model.state.standing.directional?.active, false)
        XCTAssertNil(model.state.standing.directional?.destination)
        XCTAssertEqual(model.state.standing.directional?.usesRemaining, 1, "US-6A.19: the activation is spent")
    }

    // MARK: - SCR-DI-011, the Mode A/B dashboard

    func testAModeAVehicleMakesHomeTheJourneyDashboard() async {
        let bus = approvedTuk(mode: ServiceMode.a)
        identity.live = LiveVehicle(vehicles: [bus], live: bus)
        journeys.standing = JourneyStanding(session: tripSession(), route: nil, startedByDevice: true)

        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isScheduledMode)
        XCTAssertTrue(model.state.journey.isRunning)
        XCTAssertTrue(model.state.journey.startedByDevice, "AL-32's banner")
    }

    /// **R-01** — a Mode C vehicle has no tracking session, and asking trip-state-svc about one would
    /// be the fence crossed for nothing.
    func testAModeCVehicleNeverAsksTripStateForASession() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        journeys.standing = JourneyStanding(session: tripSession())

        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.isScheduledMode)
        XCTAssertNil(model.state.journey.session)
    }

    /// **AL-32** — Start is sent whatever the tracker has done, and the session becomes ours.
    func testStartJourneySendsRegardlessOfTheTrackerAndClaimsTheSession() async {
        let bus = approvedTuk(mode: ServiceMode.a)
        identity.live = LiveVehicle(vehicles: [bus], live: bus)
        journeys.standing = JourneyStanding(session: nil)
        let model = makeModel()
        await model.refresh()

        await model.startJourney()

        XCTAssertEqual(journeys.started.count, 1)
        XCTAssertEqual(journeys.started.first?.vehicleId, testVehicleId)
        XCTAssertFalse(model.state.journey.startedByDevice, "a journey we started is not the device's")
        XCTAssertEqual(publisher.events, ["start:" + testVehicleId])
    }

    /// US-5.10's five-minute grace is the only state a restart is legal from, and it restarts rather
    /// than ends — one button, two commands, chosen by the session.
    func testTheSecondButtonRestartsInsideTheGraceAndEndsOutsideIt() async {
        let bus = approvedTuk(mode: ServiceMode.a)
        identity.live = LiveVehicle(vehicles: [bus], live: bus)
        journeys.standing = JourneyStanding(session: tripSession(state: SessionState.autoEnded, isRestartable: true))
        let model = makeModel()
        await model.refresh()
        XCTAssertTrue(model.state.journey.isRestartable)

        await model.endOrRestartJourney()
        XCTAssertEqual(journeys.restarted, [testSessionId])
        XCTAssertTrue(journeys.ended.isEmpty)

        journeys.standing = JourneyStanding(session: tripSession())
        await model.refresh()
        await model.endOrRestartJourney()
        XCTAssertEqual(journeys.ended, [testSessionId])
    }

    /// A restart keeps the distance already driven; an end starts the next journey at zero.
    func testEndingResetsTheDeviceDistanceAndRestartingKeepsIt() async {
        let bus = approvedTuk(mode: ServiceMode.a)
        identity.live = LiveVehicle(vehicles: [bus], live: bus)
        journeys.standing = JourneyStanding(session: tripSession())
        let model = makeModel()
        model.start()
        await model.refresh()

        location.emit(testFix(testHere))
        location.emit(testFix(testThere))
        let driven = model.state.journeyDistanceM
        XCTAssertGreaterThan(driven, 1_000, "1.2 km between the two fixtures, by :shared's haversine")

        await model.endOrRestartJourney()
        XCTAssertEqual(model.state.journeyDistanceM, 0)
    }

    /// A parked bus must not accumulate the drive home into the last journey's distance.
    func testDistanceOnlyAccumulatesWhileTheSessionIsRunning() async {
        let bus = approvedTuk(mode: ServiceMode.a)
        identity.live = LiveVehicle(vehicles: [bus], live: bus)
        journeys.standing = JourneyStanding(session: tripSession(state: SessionState.ended))
        let model = makeModel()
        model.start()
        await model.refresh()

        location.emit(testFix(testHere))
        location.emit(testFix(testThere))

        XCTAssertEqual(model.state.journeyDistanceM, 0)
    }

    // MARK: - The cold-start resume

    func testARideAlreadyInHandIsRestoredOnTheFirstRead() async {
        identity.live = LiveVehicle(vehicles: [approvedTuk()], live: approvedTuk())
        rides.activeRide = rideDetail(state: RideState.inProgress)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.activeRideId, testRideId)
        model.consumeActiveRide()
        XCTAssertNil(model.state.activeRideId, "returning from SCR-DI-015 must not push it again")
    }

    // MARK: - Failures

    func testAFailedVehicleReadBecomesCopyRatherThanAThrownScreen() async {
        identity.nextFailure = apiFailure(code: "vehicle-not-approved", status: 403)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_vehicle_not_approved")
        XCTAssertFalse(model.state.isLoading)
    }

    func testStoppingDropsTheLocationSubscription() {
        let model = makeModel()
        model.start()
        XCTAssertEqual(location.startCount, 1)
        model.stop()
        XCTAssertEqual(location.stopCount, 1)
    }
}

/// The derived answers on ``DriverStanding`` — US-9.1's gate and US-9.9's nudge, which two banners and
/// one offer note are all drawn from.
final class DriverStandingTests: XCTestCase {

    /// The first trip of the day is free, so the gate bites on the *next* one.
    func testTheSecondTripWarningIsSilentUntilATripHasBeenTaken() {
        var standing = DriverStanding(
            wallet: driverWallet(availableMinor: 5_000),
            dailyFee: todaysFee(dailyRateMinor: 10_000, tripsToday: 0, firstTripFree: true)
        )
        XCTAssertFalse(standing.cannotAffordNextTrip)
        XCTAssertFalse(standing.showsOfferFeeNote)

        standing.dailyFee = todaysFee(dailyRateMinor: 10_000, tripsToday: 1, firstTripFree: true)
        XCTAssertTrue(standing.cannotAffordNextTrip, "the wallet is Rs 50 and the fee is Rs 100")
        XCTAssertTrue(standing.showsOfferFeeNote)
    }

    func testAPaidDayNeverWarns() {
        let standing = DriverStanding(
            wallet: driverWallet(availableMinor: 0),
            dailyFee: todaysFee(status: DailyFeeDayStatus.paid, tripsToday: 3)
        )
        XCTAssertFalse(standing.cannotAffordNextTrip)
        XCTAssertFalse(standing.showsOfferFeeNote)
    }

    /// US-9.9's `< Rs 200` nudge comes from `:shared`'s rules, not from a number on this screen.
    func testTheLowBalanceNudgeIsSharedsThresholdAndNotALocalOne() {
        let low = DriverStanding(wallet: driverWallet(availableMinor: 15_000))
        XCTAssertEqual(low.lowBalanceThresholdMinor, 20_000, "D5' §9.4's Rs 200 default")

        let healthy = DriverStanding(wallet: driverWallet(availableMinor: 20_000))
        XCTAssertNil(healthy.lowBalanceThresholdMinor, "exactly the threshold is not below it")
    }

    func testNoWalletReadMeansNoAlertAtAll() {
        XCTAssertNil(DriverStanding().walletAlert)
        XCTAssertNil(DriverStanding().lowBalanceThresholdMinor)
    }
}
