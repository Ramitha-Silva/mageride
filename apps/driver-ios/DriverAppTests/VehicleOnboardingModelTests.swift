import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-004…004c — AL-30's resume, BR-25.4's per-step save, and the two-beat CTA.
@MainActor
final class VehicleOnboardingModelTests: XCTestCase {

    private var vehicles = FakeVehicleOnboardingRepository()
    private var captures = DocumentCaptureCoordinator()
    private var session = VehicleOnboardingSession()

    override func setUp() {
        super.setUp()
        vehicles = FakeVehicleOnboardingRepository()
        captures = DocumentCaptureCoordinator()
        session = VehicleOnboardingSession()
    }

    private func makeModel() -> VehicleOnboardingModel {
        VehicleOnboardingModel(vehicles: vehicles, captures: captures, session: session)
    }

    // MARK: - AL-30 · where the wizard opens

    /// **AL-30.** Re-opening the wizard opens the first non-verified step and *never* Step 1. The
    /// rule lives in the repository, and the model asks for it rather than deciding.
    func testResumeOpensTheStepTheRepositoryNames() async {
        vehicles.resumePoint = .resume(
            vehicleId: testVehicleId,
            registrationNumber: "ABC-1234",
            vehicleType: RideVehicleType.sedan,
            step: OnboardingStep.revenue,
            verdicts: verdicts(details: StepVerdict.verified, insurance: StepVerdict.verified),
            fields: []
        )

        let model = makeModel()
        await model.load()

        XCTAssertEqual(model.state.step, OnboardingStep.revenue)
        XCTAssertEqual(model.state.registrationNumber, "ABC-1234")
        XCTAssertEqual(model.state.vehicleType, RideVehicleType.sedan)
        XCTAssertFalse(model.state.isLoading)
    }

    /// **US-2.27.** Nothing part-way through means a **new** vehicle at Step 1/4.
    func testAFreshResumePointStartsAtStepOne() async {
        let model = makeModel()
        await model.load()

        XCTAssertEqual(model.state.step, OnboardingStep.details)
        XCTAssertNil(model.state.vehicleId)
    }

    func testAFailedResumeReadLeavesCopyRatherThanASpinner() async {
        vehicles.nextFailure = TestFailure()

        let model = makeModel()
        await model.load()

        XCTAssertFalse(model.state.isLoading)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    // MARK: - The CTA gate

    /// Step 1/4 needs a plate **and** a type; a whitespace-only plate is not a plate.
    func testStepOneNeedsBothAPlateAndAType() async {
        let model = makeModel()
        await model.load()

        XCTAssertFalse(model.state.canContinue)

        model.onRegistrationChanged("   ")
        model.onVehicleTypeChanged(RideVehicleType.threeWheeler)
        XCTAssertFalse(model.state.canContinue, "a blank plate is not a plate")

        model.onRegistrationChanged("ABC-1234")
        XCTAssertTrue(model.state.canContinue)
    }

    /// **Step 4/4 needs both photographs.** One photograph cannot show a vehicle's front and back
    /// number plates, and the plate is what the step is checked on.
    func testStepFourNeedsBothPhotographs() async {
        let model = await resumed(at: OnboardingStep.photos)

        model.apply(DocumentCaptureResult(target: .vehicleFront, image: .stub("f.jpg", CaptureSource.cameraDragCrop)))
        XCTAssertFalse(model.state.canContinue, "front only")

        model.apply(DocumentCaptureResult(target: .vehicleBack, image: .stub("b.jpg", CaptureSource.cameraDragCrop)))
        XCTAssertTrue(model.state.canContinue)
    }

    // MARK: - Saving

    /// **Δ C029 — `POST /v1/vehicles` IS Step 1/4.** A fresh wizard registers the vehicle; it does
    /// not create one and then save `details` onto it.
    func testAFreshWizardRegistersTheVehicleRatherThanSavingDetailsTwice() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.verified, nextStep: OnboardingStep.insurance)

        let model = makeModel()
        await model.load()
        model.onRegistrationChanged(" ABC-1234 ")
        model.onVehicleTypeChanged(RideVehicleType.sedan)
        await model.onContinue()

        XCTAssertEqual(vehicles.started.count, 1)
        XCTAssertEqual(vehicles.started.first?.registrationNumber, " ABC-1234 ", "trimming is the repository's")
        XCTAssertTrue(vehicles.savedDetails.isEmpty)
        XCTAssertEqual(model.state.step, OnboardingStep.insurance)
        XCTAssertEqual(session.vehicleId, testVehicleId, "SCR-DI-006 is told which vehicle it is about")
    }

    /// A driver who stepped back to Step 1/4 on a vehicle that already exists saves the step; there
    /// is no second registration.
    func testSteppingBackToStepOneSavesTheStepInsteadOfRegisteringAgain() async {
        let model = await resumed(at: OnboardingStep.insurance)
        model.onBack()
        XCTAssertEqual(model.state.step, OnboardingStep.details)

        model.onVehicleTypeChanged(RideVehicleType.van)
        await model.onContinue()

        XCTAssertTrue(vehicles.started.isEmpty)
        XCTAssertEqual(vehicles.savedDetails.count, 1)
        XCTAssertEqual(vehicles.savedDetails.first?.vehicleId, testVehicleId)
    }

    /// **A clean step does not stop for the driver.** There is nothing on the card to read.
    func testAVerifiedStepAdvancesOnTheFirstTap() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.verified, nextStep: OnboardingStep.revenue)

        let model = await resumed(at: OnboardingStep.insurance)
        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("i.jpg", CaptureSource.cameraDragCrop)))
        await model.onContinue()

        XCTAssertEqual(model.state.step, OnboardingStep.revenue)
        XCTAssertNil(model.state.savedVerdict, "the new step has not been saved")
    }

    /// **BR-25.3 — the two-beat CTA.** A step that comes back Pending leaves the driver on the card
    /// with the ⚑; a second tap continues, because a `pending_review` step is pending by design and
    /// waiting for it to clear would trap them in the wizard forever.
    func testAPendingStepStopsOnceAndContinuesOnTheSecondTap() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.pendingReview, nextStep: OnboardingStep.revenue)

        let model = await resumed(at: OnboardingStep.insurance)
        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("i.jpg", CaptureSource.cameraDragCrop)))

        await model.onContinue()
        XCTAssertEqual(model.state.step, OnboardingStep.insurance, "the driver reads the card first")
        XCTAssertTrue(model.state.isPendingReview)

        await model.onContinue()
        XCTAssertEqual(model.state.step, OnboardingStep.revenue)
        XCTAssertEqual(vehicles.savedDocuments.count, 1, "the second tap sends nothing")
    }

    /// **The server's own `nextStep` wins.** It is derived from all four verdicts, so a driver who
    /// resumed at Step 3 with Step 2 still pending is sent back rather than marched forward.
    func testTheServersNextStepBeatsTheOrdinalSuccessor() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.verified, nextStep: OnboardingStep.insurance)

        let model = await resumed(at: OnboardingStep.revenue)
        model.apply(DocumentCaptureResult(target: .revenueLicence, image: .stub("r.jpg", CaptureSource.cameraDragCrop)))
        await model.onContinue()

        XCTAssertEqual(model.state.step, OnboardingStep.insurance)
    }

    /// Step 4/4 hands over to SCR-DI-006 — even when an earlier step is still pending and the server
    /// points back at it, because SCR-DI-006 is the screen that explains a pending verdict.
    func testStepFourHandsOverToTheStatusScreen() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.verified, nextStep: OnboardingStep.insurance)

        let model = await resumed(at: OnboardingStep.photos)
        model.apply(DocumentCaptureResult(target: .vehicleFront, image: .stub("f.jpg", CaptureSource.cameraDragCrop)))
        model.apply(DocumentCaptureResult(target: .vehicleBack, image: .stub("b.jpg", CaptureSource.cameraDragCrop)))
        await model.onContinue()

        XCTAssertTrue(model.state.isSubmitted)
        XCTAssertEqual(vehicles.savedDocuments.first?.step, OnboardingStep.photos)
        XCTAssertNotNil(vehicles.savedDocuments.first?.back, "both plates go up together")
    }

    // MARK: - Δ MCS-02 · corrections

    /// **A correction needs no second photograph.** The document is already on record, so the save
    /// carries the fields and no file at all — which is the whole point of BR-25.3's edit.
    func testACorrectionOnASavedStepSendsFieldsAndNoFile() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.pendingReview)
        vehicles.status = statusResponse(
            steps: verdicts(),
            fields: [field(key: VehicleFieldKeys.insuranceExpiry, value: nil, verifyStatus: VerifyStatus.pending)]
        )

        let model = await resumed(at: OnboardingStep.insurance)
        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("i.jpg", CaptureSource.cameraDragCrop)))
        await model.onContinue()
        XCTAssertEqual(vehicles.savedDocuments.count, 1)

        model.onCorrectionChanged(key: VehicleFieldKeys.insuranceExpiry, value: "2026-12-31")
        XCTAssertTrue(model.state.hasCorrections)
        await model.onContinue()

        XCTAssertEqual(vehicles.savedDocuments.count, 1, "no second upload")
        XCTAssertEqual(vehicles.savedCorrections.count, 1)
        XCTAssertEqual(vehicles.savedCorrections.first?.corrections.insuranceExpiry, "2026-12-31")
        XCTAssertTrue(model.state.corrections.isEmpty, "cleared once sent")
    }

    /// A blank correction is not a correction, and must not be sent as one.
    func testAWhitespaceCorrectionIsDropped() async {
        let model = await resumed(at: OnboardingStep.insurance)

        model.onCorrectionChanged(key: VehicleFieldKeys.revenueNo, value: "RL-1")
        model.onCorrectionChanged(key: VehicleFieldKeys.revenueNo, value: "  ")

        XCTAssertFalse(model.state.hasCorrections)
    }

    /// **`reg_no_match` and `plate_text` are never the driver's to answer** — they are the fraud
    /// check Step 4/4 exists to perform.
    func testThePlateCheckIsNotCorrectable() {
        XCTAssertFalse(VehicleFieldKeys.correctable.contains(VehicleFieldKeys.regNoMatch))
        XCTAssertFalse(VehicleFieldKeys.correctable.contains(VehicleFieldKeys.plateText))
        XCTAssertTrue(VehicleFieldKeys.correctable.contains(VehicleFieldKeys.insuranceExpiry))
    }

    /// `OnboardingCorrections` is a named type precisely so a key the step does not accept cannot
    /// reach the request.
    func testOnlyTheFourAcceptedKeysReachTheRequest() {
        let corrections = onboardingCorrections(from: [
            VehicleFieldKeys.insuranceExpiry: "2026-12-31",
            VehicleFieldKeys.regNoMatch: "true",
            VehicleFieldKeys.plateText: "XYZ-9",
        ])

        XCTAssertEqual(corrections.insuranceExpiry, "2026-12-31")
        XCTAssertNil(corrections.revenueNo)
        XCTAssertNil(corrections.revenueExpiry)
        XCTAssertNil(corrections.insurancePolicyNo)
    }

    // MARK: - Captures

    /// **AL-43.** A tile only says which slot; the scanner is SCR-DI-005's.
    func testATileRequestsACaptureAndTheResultLandsInItsSlot() async throws {
        let model = await resumed(at: OnboardingStep.insurance)

        model.requestCapture(.insurance)
        XCTAssertEqual(captures.pending, .insurance)

        captures.deliver(.stub("insurance.jpg", CaptureSource.cameraDragCrop))
        model.apply(try XCTUnwrap(captures.result))

        XCTAssertTrue(model.state.isCaptured(.insurance))
        XCTAssertNil(captures.result, "consumed, so a redraw cannot apply it twice")
    }

    /// The two licence slots are C086's Profile Setup's. AL-27 keeps driver identity and vehicle
    /// onboarding apart, and the coordinator is shared.
    func testALicenceResultIsIgnoredByTheWizard() async {
        let model = await resumed(at: OnboardingStep.insurance)

        model.apply(DocumentCaptureResult(target: .licenceFront, image: .stub("l.jpg", CaptureSource.cameraDragCrop)))

        XCTAssertFalse(model.state.isCaptured(.insurance))
    }

    /// **A fresh capture un-saves the step**: what is on screen is no longer what was sent, so the
    /// CTA has to be a save again rather than an advance.
    func testARecaptureTurnsTheCtaBackIntoASave() async {
        vehicles.nextSavedStep = savedStep(stepStatus: StepVerdict.pendingReview)

        let model = await resumed(at: OnboardingStep.insurance)
        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("a.jpg", CaptureSource.cameraDragCrop)))
        await model.onContinue()
        XCTAssertNotNil(model.state.savedVerdict)

        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("b.jpg", CaptureSource.cameraDragCrop)))
        XCTAssertNil(model.state.savedVerdict)

        await model.onContinue()
        XCTAssertEqual(vehicles.savedDocuments.count, 2)
    }

    // MARK: - Back

    /// *"Back exits the wizard"* from Step 1/4 (D2' §SCR-DI-004); anywhere else it is a step back.
    func testBackStepsBackwardsAndOnlyLeavesFromStepOne() async {
        let model = await resumed(at: OnboardingStep.revenue)

        model.onBack()
        XCTAssertEqual(model.state.step, OnboardingStep.insurance)
        XCTAssertFalse(model.state.hasExited)

        model.onBack()
        XCTAssertEqual(model.state.step, OnboardingStep.details)

        model.onBack()
        XCTAssertTrue(model.state.hasExited)
    }

    // MARK: - Failures

    /// **D-37.** `409 registration-exists` is an inline error on the one field that has to change,
    /// not a screen-level message beside a form the driver cannot fix.
    func testATakenRegistrationIsAnInlineErrorRatherThanScreenCopy() async {
        vehicles.nextFailure = apiFailure(code: "registration-exists")

        let model = makeModel()
        await model.load()
        model.onRegistrationChanged("ABC-1234")
        model.onVehicleTypeChanged(RideVehicleType.sedan)
        await model.onContinue()

        XCTAssertTrue(model.state.isRegistrationTaken)
        XCTAssertNil(model.state.errorKey, "the plate field says it; the screen does not say it twice")

        model.onRegistrationChanged("ABC-9999")
        XCTAssertFalse(model.state.isRegistrationTaken, "typing clears it")
    }

    /// Anything else is screen-level copy, resolved from the code (D-26).
    func testAnyOtherFailureIsResolvedCopy() async {
        vehicles.nextFailure = apiFailure(code: "mode-not-allowed", status: 403)

        let model = makeModel()
        await model.load()
        model.onRegistrationChanged("ABC-1234")
        model.onVehicleTypeChanged(RideVehicleType.sedan)
        await model.onContinue()

        XCTAssertEqual(model.state.errorKey, "error_mode_not_allowed")
        XCTAssertFalse(model.state.isRegistrationTaken)
    }

    // MARK: - The extract card

    /// Each step's card lists only its own keys, in the order the wireframe draws them.
    func testTheExtractCardListsThisStepsKeysInWireframeOrder() async {
        vehicles.resumePoint = .resume(
            vehicleId: testVehicleId,
            registrationNumber: "ABC-1234",
            vehicleType: RideVehicleType.sedan,
            step: OnboardingStep.revenue,
            verdicts: verdicts(),
            fields: [
                field(key: VehicleFieldKeys.revenueExpiry, value: "2026-09-30"),
                field(key: VehicleFieldKeys.insuranceExpiry, value: "2026-12-31"),
                field(key: VehicleFieldKeys.revenueNo, value: "RL-558231"),
            ]
        )

        let model = makeModel()
        await model.load()

        XCTAssertEqual(
            model.state.stepFields.map(\.key),
            [VehicleFieldKeys.revenueNo, VehicleFieldKeys.revenueExpiry],
            "the insurance field belongs to another step's card"
        )
    }

    /// The confidence row is the step's **weakest** field — a step is only as verified as its worst.
    func testTheConfidenceRowIsTheWeakestFieldInTheStep() async {
        vehicles.resumePoint = .resume(
            vehicleId: testVehicleId,
            registrationNumber: "ABC-1234",
            vehicleType: RideVehicleType.sedan,
            step: OnboardingStep.revenue,
            verdicts: verdicts(),
            fields: [
                field(key: VehicleFieldKeys.revenueNo, value: "RL-558231"),
                field(key: VehicleFieldKeys.revenueExpiry, value: nil, verifyStatus: VerifyStatus.pending),
            ]
        )

        let model = makeModel()
        await model.load()

        XCTAssertEqual(model.state.lowestConfidence ?? 0, 0.42, accuracy: 0.001)
    }

    // MARK: -

    /// A model already resumed onto [step] with a vehicle behind it.
    private func resumed(at step: OnboardingStep) async -> VehicleOnboardingModel {
        vehicles.resumePoint = .resume(
            vehicleId: testVehicleId,
            registrationNumber: "ABC-1234",
            vehicleType: RideVehicleType.sedan,
            step: step,
            verdicts: verdicts(),
            fields: []
        )
        let model = makeModel()
        await model.load()
        return model
    }
}
