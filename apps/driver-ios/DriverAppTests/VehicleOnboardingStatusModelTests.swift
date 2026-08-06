import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-006 — the four verdicts, the auto-approval it only reports, and the vehicle nobody named.
@MainActor
final class VehicleOnboardingStatusModelTests: XCTestCase {

    private var vehicles = FakeVehicleOnboardingRepository()
    private var session = VehicleOnboardingSession()

    override func setUp() {
        super.setUp()
        vehicles = FakeVehicleOnboardingRepository()
        session = VehicleOnboardingSession()
    }

    private func makeModel() -> VehicleOnboardingStatusModel {
        VehicleOnboardingStatusModel(vehicles: vehicles, session: session)
    }

    /// The screen reads two things: the verdicts, and the vehicle whose plate and type the header
    /// shows. The onboarding-status read carries neither, which is why there are two.
    func testTheHeaderAndTheVerdictsComeFromTwoReads() async {
        session.open(testVehicleId)
        vehicles.status = statusResponse(
            steps: verdicts(details: StepVerdict.verified, insurance: StepVerdict.verified)
        )
        vehicles.vehicleDetail = detail(registrationNumber: "ABC-1234", vehicleType: VehicleType.sedan)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.registrationNumber, "ABC-1234")
        XCTAssertEqual(model.state.vehicleType, VehicleType.sedan)
        XCTAssertEqual(model.state.rows.count, 4)
        XCTAssertFalse(model.state.isLoading)
    }

    /// The wireframe's *"⚠ 1 pending"* counts everything that is not Verified — an officer's queue
    /// and a step the driver has not uploaded both leave the vehicle short of approval.
    func testThePendingCountIsEverythingNotVerified() async {
        session.open(testVehicleId)
        vehicles.status = statusResponse(
            steps: verdicts(
                details: StepVerdict.verified,
                insurance: StepVerdict.verified,
                revenue: StepVerdict.verified,
                photos: StepVerdict.pendingReview
            )
        )

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.pendingCount, 1)
        XCTAssertFalse(model.state.isApproved)
    }

    /// **AL-30 — approval is two questions.** `onboardingStatus` says the four steps are done and
    /// `status` says the registration stands; C029's decision (5) is why they can disagree.
    func testApprovalNeedsBothTheRegistrationAndTheOnboardingStatus() async {
        session.open(testVehicleId)
        vehicles.status = statusResponse(
            steps: verdicts(
                details: StepVerdict.verified,
                insurance: StepVerdict.verified,
                revenue: StepVerdict.verified,
                photos: StepVerdict.verified
            ),
            status: RegistrationStatus.approved,
            onboardingStatus: OnboardingStatus.incomplete
        )

        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.isApproved, "an approved registration with incomplete onboarding is not approved")
        XCTAssertEqual(model.state.pendingCount, 0)
    }

    /// **US-2.10.** A step that is not yet *saved* is the driver's to finish; one that is saved and
    /// pending is an officer's. Only the first offers Resume.
    func testResumeIsOfferedOnlyForAStepTheDriverCanStillFinish() async {
        session.open(testVehicleId)
        vehicles.status = statusResponse(steps: verdicts(details: StepVerdict.verified, insurance: StepVerdict.pendingReview, revenue: StepVerdict.pendingReview, photos: StepVerdict.pendingReview))

        let model = makeModel()
        await model.refresh()
        XCTAssertFalse(model.state.canResume, "every outstanding step is with an officer")

        vehicles.status = statusResponse(steps: verdicts(details: StepVerdict.verified, insurance: StepVerdict.pendingInput))
        await model.refresh()
        XCTAssertTrue(model.state.canResume)
    }

    /// US-2.15 — rejected, with the officer's reason, and the driver has to re-upload.
    func testARejectionCarriesItsReason() async {
        session.open(testVehicleId)
        vehicles.status = statusResponse(steps: verdicts(), status: RegistrationStatus.rejected)
        vehicles.vehicleDetail = detail(status: RegistrationStatus.rejected, rejectionReason: "Plate unreadable")

        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isRejected)
        XCTAssertEqual(model.state.rejectionReason, "Plate unreadable")
    }

    /// **Not an error.** A restore onto a deactivated vehicle looks exactly like this, and the screen
    /// offers a way back rather than a failure the driver cannot act on.
    func testNoNamedVehicleIsAnEmptyStateRatherThanAnError() async {
        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isUnknownVehicle)
        XCTAssertNil(model.state.errorKey)
        XCTAssertFalse(model.state.isLoading)
        XCTAssertTrue(vehicles.statusReads.isEmpty, "nothing to read")
    }

    func testAFailedReadResolvesToCopy() async {
        session.open(testVehicleId)
        vehicles.nextFailure = apiFailure(code: "vehicle-not-found", status: 404)

        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_vehicle_not_found")
    }

    /// The refresh button is the whole point of the screen after the first read: a Pending document
    /// is confirmed minutes or days later, and US-2.14's push is what brings the driver back.
    func testRefreshReadsAgain() async {
        session.open(testVehicleId)

        let model = makeModel()
        await model.refresh()
        await model.refresh()

        XCTAssertEqual(vehicles.statusReads.count, 2)
    }
}
