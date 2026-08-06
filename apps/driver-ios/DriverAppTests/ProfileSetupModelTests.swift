import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-003a — driver identity, the two-beat save, and AL-29's manual path.
@MainActor
final class ProfileSetupModelTests: XCTestCase {

    private func makeModel(
        profiles: FakeDriverProfileRepository = FakeDriverProfileRepository()
    ) -> (ProfileSetupModel, DocumentCaptureCoordinator) {
        let captures = DocumentCaptureCoordinator()
        return (ProfileSetupModel(profiles: profiles, captures: captures), captures)
    }

    /// **AL-27 / US-2.12.** Name, photo and both licence sides. The photo in particular is the one
    /// field whose absence a driver will not otherwise notice — the CTA simply stays dead.
    func testSaveNeedsANameAPhotoAndBothLicenceSides() {
        let (model, _) = makeModel()
        XCTAssertFalse(model.state.canSave)

        model.onNameChanged("K. Fernando")
        XCTAssertFalse(model.state.canSave, "no photo")

        model.onPhotoPicked(.stub("profile-photo.jpg", CaptureSource.gallery))
        XCTAssertFalse(model.state.canSave, "no licence")

        model.apply(DocumentCaptureResult(target: .licenceFront, image: .stub("f.jpg", CaptureSource.cameraDragCrop)))
        XCTAssertFalse(model.state.canSave, "one side only")

        model.apply(DocumentCaptureResult(target: .licenceBack, image: .stub("b.jpg", CaptureSource.cameraDragCrop)))
        XCTAssertTrue(model.state.canSave)
    }

    /// A whitespace-only name is not a name.
    func testABlankNameDoesNotCompleteTheDraft() {
        let (model, _) = makeModel()
        fill(model, name: "   ")
        XCTAssertFalse(model.state.canSave)
    }

    /// **AL-43.** The tile opens SCR-DI-005; the coordinator is the only thing that survives the
    /// trip, and consuming the result is what stops it being applied twice.
    func testATileRequestsACaptureAndTheResultLandsInItsSlot() throws {
        let (model, captures) = makeModel()

        model.requestCapture(.licenceFront)
        XCTAssertEqual(captures.pending, .licenceFront)

        captures.deliver(.stub("licence-front.jpg", CaptureSource.cameraDragCrop))
        XCTAssertNil(captures.pending)
        XCTAssertNotNil(captures.result)

        model.apply(try XCTUnwrap(captures.result))
        XCTAssertNotNil(model.state.draft.licenceFront)
        XCTAssertNil(captures.result, "consumed, so a redraw cannot apply it again")
    }

    /// The four vehicle slots are C087's wizard's. The coordinator is shared, and a result this
    /// screen did not ask for is not this screen's.
    func testAVehicleDocumentResultIsIgnored() {
        let (model, _) = makeModel()

        model.apply(DocumentCaptureResult(target: .insurance, image: .stub("i.jpg", CaptureSource.cameraDragCrop)))

        XCTAssertNil(model.state.draft.licenceFront)
        XCTAssertNil(model.state.draft.licenceBack)
    }

    // MARK: - The two-beat save

    /// `PUT /v1/drivers/profile` is what *queues* the extraction, so the card cannot exist before
    /// the first save. Nothing doubtful came back, so there is nothing to review and the screen
    /// continues on that same save.
    func testAFirstSaveWithEverythingConfirmedContinuesImmediately() async {
        let profiles = FakeDriverProfileRepository()
        profiles.extraction = .allConfirmed
        let (model, _) = makeModel(profiles: profiles)
        fill(model)

        await model.save()

        XCTAssertEqual(profiles.submitted.count, 1)
        XCTAssertTrue(model.state.isDone)
        XCTAssertFalse(model.state.hasOfficerFlag)
    }

    /// BR-25.2: a field that is doubtful, unread or typed leaves the driver on the card — skipping
    /// past it would hide the ⚑.
    func testAPendingFieldKeepsTheDriverOnTheCard() async {
        let profiles = FakeDriverProfileRepository()
        profiles.extraction = .nicPending
        let (model, _) = makeModel(profiles: profiles)
        fill(model)

        await model.save()

        XCTAssertFalse(model.state.isDone)
        XCTAssertTrue(model.state.hasOfficerFlag, "the ⚑ banner is up")
        XCTAssertNotNil(model.state.extraction)
    }

    /// A `manual` field is pending **by design** (AL-29, US-2.4a), so waiting for it to clear would
    /// trap the driver on this screen forever. The correction goes up and the screen continues.
    func testACorrectionIsSentAndThenTheScreenContinues() async {
        let profiles = FakeDriverProfileRepository()
        profiles.extraction = .nicPending
        let (model, _) = makeModel(profiles: profiles)
        fill(model)
        await model.save()

        model.onNicChanged("199012345678")
        await model.save()

        XCTAssertEqual(profiles.submitted.count, 2)
        XCTAssertEqual(profiles.submitted.last?.nicNo, "199012345678")
        XCTAssertTrue(model.state.isDone)
    }

    /// Re-running the extraction over the same images would only produce the same verdicts, so a
    /// second tap with nothing new to send simply continues.
    func testASecondTapWithNothingNewDoesNotResubmit() async {
        let profiles = FakeDriverProfileRepository()
        profiles.extraction = .nicPending
        let (model, _) = makeModel(profiles: profiles)
        fill(model)
        await model.save()

        await model.save()

        XCTAssertEqual(profiles.submitted.count, 1)
        XCTAssertTrue(model.state.isDone)
    }

    /// **The client never claims a provenance.** Everything the driver typed is sent as a plain
    /// value; registry-svc is what stamps `source='manual'` and `verify_status='pending'`, because
    /// a client that could claim `source='ai'` would make AL-29 advisory.
    func testEveryDriverTypedValueIsSentAsIs() async {
        let profiles = FakeDriverProfileRepository()
        profiles.extraction = .nicPending
        let (model, _) = makeModel(profiles: profiles)
        fill(model)
        await model.save()

        model.onNicChanged("199012345678")
        model.onLicenceFieldChanged(key: LicenceFieldKeys.licenceNo, value: "B1234567")
        model.onLicenceFieldChanged(key: LicenceFieldKeys.licenceExpiry, value: "2028-04")
        model.onAllowedVehicleTypesChanged([VehicleType.sedan, VehicleType.van])
        await model.save()

        let sent = profiles.submitted.last
        XCTAssertEqual(sent?.nicNo, "199012345678")
        XCTAssertEqual(sent?.licenceNo, "B1234567")
        XCTAssertEqual(sent?.licenceExpiry, "2028-04")
        XCTAssertEqual(sent?.allowedVehicleTypes?.map(\.wire), ["sedan", "van"])
    }

    func testAFailedSaveShowsCopyAndLeavesTheFormIntact() async {
        let profiles = FakeDriverProfileRepository()
        profiles.submitFailure = TestFailure()
        let (model, _) = makeModel(profiles: profiles)
        fill(model)

        await model.save()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isDone)
        XCTAssertFalse(model.state.isBusy)
        XCTAssertTrue(model.state.canSave, "the driver can try again without refilling anything")
    }

    /// The ✎ opens one row at a time; a second tap closes it.
    func testTheEditToggleOpensAndClosesOneRow() {
        let (model, _) = makeModel()

        model.toggleEdit(LicenceFieldKeys.licenceNo)
        XCTAssertEqual(model.state.editingKey, LicenceFieldKeys.licenceNo)

        model.toggleEdit(LicenceFieldKeys.licenceExpiry)
        XCTAssertEqual(model.state.editingKey, LicenceFieldKeys.licenceExpiry)

        model.toggleEdit(LicenceFieldKeys.licenceExpiry)
        XCTAssertNil(model.state.editingKey)
    }

    // MARK: - The extract card's own rules

    /// The four rows are the ones `registry.document_fields` stores (AL-29) and they appear in the
    /// card's order, whatever order the server answered in.
    func testTheCardShowsTheFourKeysInTheCardsOrder() {
        let extraction = ApiDriverProfileRepository.extraction(
            of: [
                ExtractedField(
                    key: LicenceFieldKeys.nicNo,
                    value: "199012345678",
                    source: FieldSource.ai,
                    confidence: 0.94,
                    verifyStatus: VerifyStatus.confirmed
                ),
            ],
            fallbackName: "K. Fernando"
        )

        XCTAssertEqual(extraction.fields.map(\.key), LicenceFieldKeys.order)
        XCTAssertEqual(extraction.field(LicenceFieldKeys.nicNo)?.value, "199012345678")
        XCTAssertFalse(extraction.field(LicenceFieldKeys.nicNo)?.needsOfficerReview ?? true)
    }

    /// A key the server did not answer for at all has not been verified either; the screen shows it
    /// as unread, which is the same prompt to type it in.
    func testAKeyTheServerDidNotAnswerForIsUnreadAndFlagged() {
        let extraction = ApiDriverProfileRepository.extraction(of: [], fallbackName: "K. Fernando")

        XCTAssertTrue(extraction.needsReview)
        XCTAssertTrue(extraction.hasOfficerFlag)
        for field in extraction.fields {
            XCTAssertNil(field.value)
            XCTAssertTrue(field.needsOfficerReview)
        }
    }

    /// AL-29's whole point: a `manual` field is what routes to the Verification-Officer queue.
    func testAManualFieldIsFlaggedForTheOfficerQueue() {
        let extraction = ApiDriverProfileRepository.extraction(
            of: LicenceFieldKeys.order.map {
                ExtractedField(
                    key: $0,
                    value: "typed",
                    source: FieldSource.manual,
                    confidence: nil,
                    verifyStatus: VerifyStatus.pending
                )
            },
            fallbackName: "K. Fernando"
        )

        XCTAssertTrue(extraction.hasOfficerFlag)
        XCTAssertTrue(extraction.fields.allSatisfy(\.isManual))
    }

    // MARK: -

    private func fill(_ model: ProfileSetupModel, name: String = "K. Fernando") {
        model.onNameChanged(name)
        model.onPhotoPicked(.stub("profile-photo.jpg", CaptureSource.gallery))
        model.apply(DocumentCaptureResult(target: .licenceFront, image: .stub("f.jpg", CaptureSource.cameraDragCrop)))
        model.apply(DocumentCaptureResult(target: .licenceBack, image: .stub("b.jpg", CaptureSource.cameraDragCrop)))
    }
}
