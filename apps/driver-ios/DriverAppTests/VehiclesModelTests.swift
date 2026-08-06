import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-026 / 026a — the two groups, US-9.6's go-live gate and D-03's single publisher.
@MainActor
final class VehiclesModelTests: XCTestCase {

    private var vehicles = FakeVehicleOnboardingRepository()
    private var session = VehicleOnboardingSession()
    private var activeVehicle = FakeActiveVehicleStore()

    override func setUp() {
        super.setUp()
        vehicles = FakeVehicleOnboardingRepository()
        session = VehicleOnboardingSession()
        activeVehicle = FakeActiveVehicleStore()
    }

    private func makeModel() -> VehiclesModel {
        VehiclesModel(vehicles: vehicles, session: session, activeVehicle: activeVehicle)
    }

    /// **AL-27.** Owned is Mode C; anything else in this driver's list arrived by assignment or
    /// share, because there is no other way for one to be there.
    func testTheTwoGroupsAreSplitOnMode() async {
        vehicles.vehicles = [
            summary(),
            summary(vehicleId: testOtherVehicleId, registrationNumber: "VN-3321", mode: ServiceMode.b),
        ]

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.owned.map(\.vehicleId), [testVehicleId])
        XCTAssertEqual(model.state.assigned.map(\.vehicleId), [testOtherVehicleId])
    }

    /// **US-9.6.** An owned Mode C vehicle needs *both* an approved registration and approved
    /// onboarding; an assigned Mode A/B one is eligible on the strength of being assigned at all,
    /// because the Fleet Portal approved it.
    func testOnlyAnApprovedModeCVehicleCanGoLive() async {
        vehicles.vehicles = [
            summary(status: RegistrationStatus.approved, onboardingStatus: OnboardingStatus.incomplete),
            summary(
                vehicleId: testOtherVehicleId,
                mode: ServiceMode.a,
                status: RegistrationStatus.approved,
                onboardingStatus: OnboardingStatus.incomplete
            ),
        ]

        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.owned[0].canGoLive, "approved registration, onboarding still incomplete")
        XCTAssertTrue(model.state.assigned[0].canGoLive)
        XCTAssertTrue(model.state.canGoOnline)
    }

    /// **D-03.** Selecting sets the single active publisher, and a vehicle that cannot be dispatched
    /// is not selectable at all — a selection the go-online toggle then rejected would be a state the
    /// driver cannot understand.
    func testSelectingIsRefusedForAVehicleThatCannotGoLive() async {
        vehicles.vehicles = [summary()]

        let model = makeModel()
        await model.refresh()
        model.select(model.state.owned[0])

        XCTAssertNil(activeVehicle.activeVehicleId)
        XCTAssertNil(model.state.activeVehicleId)
    }

    func testSelectingAnEligibleVehicleWritesTheStore() async {
        vehicles.vehicles = [
            summary(status: RegistrationStatus.approved, onboardingStatus: OnboardingStatus.approved),
        ]

        let model = makeModel()
        await model.refresh()
        model.select(model.state.owned[0])

        XCTAssertEqual(activeVehicle.activeVehicleId, testVehicleId)
        XCTAssertEqual(model.state.activeVehicleId, testVehicleId)
    }

    /// A vehicle deactivated on another handset must not stay selected here: going online as one the
    /// platform has retired is a connection that fails with no way for the driver to see why.
    func testASelectionThatIsNoLongerInTheListIsDropped() async {
        activeVehicle.activeVehicleId = testOtherVehicleId
        vehicles.vehicles = [summary()]

        let model = makeModel()
        await model.refresh()

        XCTAssertNil(activeVehicle.activeVehicleId)
        XCTAssertNil(model.state.activeVehicleId)
    }

    /// The wireframe prints *"Incomplete · Step 3 of 4"*, so the resume point is read per incomplete
    /// row — and only for those, rather than a second request per vehicle.
    func testTheStepNumberIsReadOnlyForAnIncompleteVehicle() async {
        vehicles.vehicles = [
            summary(),
            summary(
                vehicleId: testOtherVehicleId,
                status: RegistrationStatus.approved,
                onboardingStatus: OnboardingStatus.approved
            ),
        ]
        vehicles.status = statusResponse(steps: verdicts(details: StepVerdict.verified), nextStep: OnboardingStep.insurance)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(vehicles.statusReads, [testVehicleId])
        XCTAssertEqual(model.state.owned.first(where: { $0.vehicleId == testVehicleId })?.nextStepNumber, 2)
    }

    /// An empty list is what raises SCR-DI-026a, and *"Not now"* leaves the empty list behind it.
    func testAnEmptyListRaisesTheOnboardPrompt() async {
        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isEmpty)
        XCTAssertTrue(model.state.isOnboardPromptVisible)

        model.dismissOnboardPrompt()
        XCTAssertFalse(model.state.isOnboardPromptVisible)
        XCTAssertTrue(model.state.isEmpty, "the empty state stays; only the alert goes")
    }

    /// **US-2.16.** Removing is confirmed first, then it clears both holders that could still be
    /// pointing at the vehicle — a status screen restored onto a deleted vehicle would render a 404.
    func testDeactivatingClearsTheSessionAndTheSelection() async {
        vehicles.vehicles = [
            summary(status: RegistrationStatus.approved, onboardingStatus: OnboardingStatus.approved),
        ]
        activeVehicle.activeVehicleId = testVehicleId
        session.open(testVehicleId)

        let model = makeModel()
        await model.refresh()
        model.confirmDeactivate(model.state.owned[0])
        XCTAssertNotNil(model.state.deactivating)

        vehicles.vehicles = []
        await model.deactivate()

        XCTAssertEqual(vehicles.deactivated, [testVehicleId])
        XCTAssertNil(session.vehicleId)
        XCTAssertNil(activeVehicle.activeVehicleId)
        XCTAssertNil(model.state.deactivating)
    }

    func testCancellingTheConfirmSendsNothing() async {
        vehicles.vehicles = [summary()]

        let model = makeModel()
        await model.refresh()
        model.confirmDeactivate(model.state.owned[0])
        model.cancelDeactivate()
        await model.deactivate()

        XCTAssertTrue(vehicles.deactivated.isEmpty)
    }

    func testAFailedReadResolvesToCopy() async {
        vehicles.nextFailure = apiFailure(code: "not-owner", status: 403)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_not_owner")
        XCTAssertFalse(model.state.isLoading)
    }

    /// Opening a row is what names the vehicle for SCR-DI-006 — the route carries no argument.
    func testOpeningARowNamesTheVehicleForTheStatusScreen() async {
        vehicles.vehicles = [summary()]

        let model = makeModel()
        await model.refresh()
        model.open(model.state.owned[0])

        XCTAssertEqual(session.vehicleId, testVehicleId)
    }

    /// The fleet caption is US-13.9's, and it is absent on an owned vehicle. An open-ended assignment
    /// shows the fleet alone — a real state rather than a missing value.
    func testTheAssignmentCaptionIsAbsentOnAnOwnedVehicle() async {
        vehicles.vehicles = [
            summary(),
            summary(
                vehicleId: testOtherVehicleId,
                mode: ServiceMode.b,
                fleetName: "Lanka Fleet (Pvt) Ltd"
            ),
        ]

        let model = makeModel()
        await model.refresh()

        XCTAssertNil(model.state.owned[0].assignmentCaption)
        XCTAssertEqual(model.state.assigned[0].assignmentCaption, "Lanka Fleet (Pvt) Ltd")
    }
}
