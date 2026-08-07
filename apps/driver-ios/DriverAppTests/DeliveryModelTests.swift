import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-016a/b/c** — the three delivery sheets over the package ride machine (AL-33).
///
/// The DoD lines these carry, sheet for sheet with `DeliveryViewModelTest.kt`: *a correct pickup OTP
/// advances to sheet 3 and notifies the recipient*, *a 6th wrong OTP shows the locked state and the
/// admin-queue message*, and *Cancel on sheet 1 re-dispatches and returns the driver to standby*.
@MainActor
final class DeliveryModelTests: XCTestCase {

    private var deliveries: FakeDeliveryRepository!
    private var contact: FakeRideContact!
    private var location: FakeDriverLocationSource!
    private var proofs: ProofUploadQueue!
    private var captures: DocumentCaptureCoordinator!

    override func setUp() {
        super.setUp()
        deliveries = FakeDeliveryRepository()
        contact = FakeRideContact()
        location = FakeDriverLocationSource()
        proofs = ProofUploadQueue()
        captures = DocumentCaptureCoordinator()
    }

    private func makeModel() -> DeliveryModel {
        DeliveryModel(
            rideId: testRideId,
            deliveries: deliveries,
            contact: contact,
            location: location,
            proofs: proofs,
            captures: captures
        )
    }

    // MARK: - The three sheets

    /// Sheet 1 → 2 → 3, and **Start delivery sends nothing**: the parcel is released by the sender's
    /// code, and D5' §11 takes a package straight from `Accepted` to `InProgress` with no arrival
    /// marker. `package.picked_up` is also the event that carries the recipient their own code
    /// (AL-21, US-20.5), which is why the client makes this call and no other.
    func testTheSheetsRunReviewThenPickupThenComplete() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted)
        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.sheet, .review)

        await model.advance()
        XCTAssertEqual(model.state.sheet, .pickup)

        model.onOtpChange("4821")
        await model.advance()

        XCTAssertEqual(deliveries.pickupOtps, ["4821"])
        XCTAssertTrue(deliveries.deliveryOtps.isEmpty)
        XCTAssertEqual(model.state.sheet, .complete)
        XCTAssertEqual(model.state.rideState, RideState.inProgress)
        XCTAssertEqual(model.state.otp, "", "the boxes are cleared for the recipient's code")
    }

    /// A driver whose app was killed between the two doors comes back to the sheet the **ride** is on:
    /// `package.picked_up` is the `→ InProgress` move, so nothing local is remembered.
    func testResumingADeliveryAlreadyInTransitOpensTheLastSheet() async {
        deliveries.detailToReturn = packageRide(state: RideState.inProgress, version: 6)
        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.sheet, .complete)
        XCTAssertTrue(model.state.isPickedUp)
    }

    /// **AL-33 decoupled the cash from the handover.** *"Delivery completed"* replaces *"Cash received
    /// (COD)"*, and an uncollected COD is a 24-hour timer's problem (P-14) rather than this button's —
    /// so the delivery ends at `Completed` with nothing on these sheets settling money.
    func testTheDeliveryOtpHandsTheParcelOverAndReturnsTheDriverToStandby() async {
        deliveries.detailToReturn = packageRide(state: RideState.inProgress, version: 2)
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.sheet, .complete)

        model.onOtpChange("4821")
        await model.advance()

        XCTAssertEqual(deliveries.deliveryOtps, ["4821"])
        XCTAssertTrue(model.state.isHandedOver)
        XCTAssertTrue(model.state.isFinished)
    }

    /// A COD delivery sitting at `PaymentPending` is **off this driver's hands**: waiting for a terminal
    /// ride state would hold a courier on a doorstep for a reconciliation that happens elsewhere.
    func testAPaymentPendingDeliveryIsFinishedForTheDriver() async {
        deliveries.detailToReturn = packageRide(state: RideState.paymentPending, version: 6)
        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isHandedOver)
        XCTAssertTrue(model.state.isFinished)
    }

    // MARK: - P-07, the two OTP gates

    /// The attempt that **spends** the budget is the one that raises the queue item — ride-svc says so,
    /// and `PackageHandoff` locks on the same count. So there is no sixth request to make: the sheet is
    /// already showing the admin-queue message and the entry is disabled behind it.
    func testTheFifthWrongCodeLocksTheGateAndNamesTheAdminQueue() async {
        deliveries.detailToReturn = packageRide(state: RideState.driverArrived)
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.sheet, .pickup)

        for attempt in 1...Int(PackageHandoff.companion.MAX_OTP_ATTEMPTS) {
            deliveries.nextFailure = invalidOtpFailure()
            model.onOtpChange("0000")
            await model.advance()
            XCTAssertEqual(model.state.attemptsUsed, attempt)
        }

        XCTAssertTrue(model.state.isLocked)
        XCTAssertEqual(model.state.attemptsRemaining, 0)
        XCTAssertEqual(model.state.errorKey, "delivery_otp_wrong")
        XCTAssertFalse(model.state.canAdvance, "there is no sixth box")

        let spent = deliveries.pickupOtps.count
        model.onOtpChange("0000")
        await model.advance()
        XCTAssertEqual(deliveries.pickupOtps.count, spent, "a locked gate sends nothing")
    }

    /// The server's count is authoritative because it survives what this one does not: a reinstall, or
    /// a driver resuming the same handoff on a second handset.
    func testAServerLockoutLocksTheGateEvenWithAttemptsLeftOnThisDevice() async {
        deliveries.detailToReturn = packageRide(state: RideState.driverArrived)
        let model = makeModel()
        await model.refresh()

        deliveries.nextFailure = lockedFailure()
        model.onOtpChange("0000")
        await model.advance()

        XCTAssertTrue(model.state.isLocked)
        XCTAssertEqual(model.state.attemptsRemaining, 0, "the server says the budget is gone, so it is")
        XCTAssertEqual(model.state.errorKey, "delivery_otp_locked")
    }

    /// `canSubmit` refuses a malformed entry **without spending an attempt** — the budget exists to stop
    /// guessing, and a typo the client can see is not a guess.
    func testAMalformedCodeIsRefusedWithoutSpendingAnAttempt() async {
        deliveries.detailToReturn = packageRide(state: RideState.driverArrived)
        let model = makeModel()
        await model.refresh()

        model.onOtpChange("48")
        await model.advance()

        XCTAssertTrue(deliveries.pickupOtps.isEmpty)
        XCTAssertEqual(model.state.attemptsRemaining, Int(PackageHandoff.companion.MAX_OTP_ATTEMPTS))
        XCTAssertFalse(model.state.canAdvance)
    }

    /// **Δ C089.** The Android twin clears the four boxes on *every* fold, and its five-second poll is a
    /// fold — so a courier typing the recipient's code there watches it disappear under them. A poll
    /// that changed nothing changes nothing here; a **transition** is what clears them, which is the
    /// rule C071's own assertion is about.
    ///
    /// Asserted on the state directly rather than through the model, because reproducing it through the
    /// model would mean waiting five seconds for a poll — and the rule is the state's, not the loop's.
    func testOnlyATransitionClearsTheTypedCode() {
        var state = DeliveryState(ride: packageRide(state: RideState.inProgress, version: 4))
        state.otp = "482"

        state.advance(to: snapshot(RideState.inProgress, 4), gates: nil)
        XCTAssertEqual(state.otp, "482", "a poll that moved nothing must not empty the boxes")

        state.advance(to: snapshot(RideState.completed, 5), gates: nil)
        XCTAssertEqual(state.otp, "")
    }

    /// A delivery that has been handed over never un-hands-over: a late poll landing after the driver has
    /// been sent back to standby must not undo it.
    func testHandedOverIsSticky() {
        var state = DeliveryState(ride: packageRide(state: RideState.inProgress, version: 4))
        state.advance(to: snapshot(RideState.completed, 5), gates: nil)
        XCTAssertTrue(state.isFinished)

        state.advance(to: snapshot(RideState.inProgress, 6), gates: nil)
        XCTAssertTrue(state.isFinished, "a late poll must not resurrect a delivery the driver has left")
    }

    // MARK: - P-10, the photograph

    /// A photograph completes the delivery on its own when nobody is there to read the code out
    /// (Δ C037), and **no delivery OTP is typed at all**.
    func testAPhotoCompletesTheDeliveryWhenTheRecipientIsAbsent() async {
        deliveries.detailToReturn = packageRide(state: RideState.inProgress, version: 4)
        let model = makeModel()
        await model.refresh()

        // Through the coordinator, because that is the seam: the sheet asks, SCR-DI-005 delivers, and
        // the sheet consumes so the same photograph cannot be applied twice.
        model.requestProofCapture()
        XCTAssertEqual(captures.pending, .deliveryProof)
        captures.deliver(testProofImage())
        guard let captured = captures.result else { return XCTFail("SCR-DI-005 delivered nothing") }
        model.apply(captured)
        XCTAssertNil(captures.result, "a delivered result is consumed by the screen that asked for it")
        XCTAssertNotNil(model.state.proof)

        await model.advance()

        XCTAssertEqual(deliveries.proofsUploaded.count, 1)
        XCTAssertTrue(deliveries.deliveryOtps.isEmpty)
        XCTAssertTrue(model.state.isFinished)
        XCTAssertNil(proofs.pending(for: testRideId), "an uploaded proof is not kept (§4.3)")
    }

    /// The upload fails on a bad signal and the driver retries **without re-photographing** — which is
    /// the whole reason the queue exists. Losing the picture would mean going back to the door.
    func testAFailedUploadKeepsThePhotographSoARetryNeedsNoCamera() async {
        deliveries.detailToReturn = packageRide(state: RideState.inProgress, version: 4)
        let model = makeModel()
        await model.refresh()

        model.apply(DocumentCaptureResult(target: .deliveryProof, image: testProofImage()))
        deliveries.nextFailure = apiFailure(code: "server-error", status: 503)
        await model.advance()

        XCTAssertFalse(model.state.isFinished, "a delivery that did not upload is not delivered")
        XCTAssertNotNil(model.state.proof, "the driver must not have to re-photograph a door")
        XCTAssertEqual(proofs.pending(for: testRideId)?.attempts, 1)
        XCTAssertEqual(proofs.pending(for: testRideId)?.state, .pending)
        XCTAssertNotNil(model.state.errorKey)
    }

    /// A capture this screen did not ask for is left alone rather than consumed out from under the
    /// screen that did — the rule `VehicleOnboardingModel.apply(_:)` states, applied to the one target
    /// that is not a document.
    func testACaptureForAnotherSlotIsNotTakenAsProof() async {
        deliveries.detailToReturn = packageRide(state: RideState.inProgress)
        let model = makeModel()
        await model.refresh()

        model.apply(DocumentCaptureResult(target: .licenceFront, image: testProofImage("licence-front.jpg")))

        XCTAssertNil(model.state.proof)
        XCTAssertNil(proofs.pending(for: testRideId))
    }

    /// The photograph outlives the view that took it: SCR-DI-005 is a full-screen takeover, so the queue
    /// is where a proof waits for the sheet to come back.
    func testAModelRebuiltAfterTheScannerFindsItsPhotographWaiting() async {
        proofs.enqueue(rideId: testRideId, image: testProofImage(), at: nil, capturedAt: Date())

        let model = makeModel()

        XCTAssertNotNil(model.state.proof)
    }

    // MARK: - Sheet 1's Cancel, and the two call buttons

    /// **R-14** — the version echoed is the one the sheet was showing, never a bumped one. What the
    /// server does with it is the half a client does not own: AL-33's re-dispatch has no route (see the
    /// repository's own note), so this releases the delivery from *this* driver and no more.
    func testCancelOnSheetOneReleasesTheJobAndReturnsTheDriverToStandby() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted, version: 7)
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.sheet, .review)

        await model.cancel()

        XCTAssertEqual(deliveries.released, [7])
        XCTAssertTrue(model.state.isFinished)
        XCTAssertNil(proofs.pending(for: testRideId))
    }

    /// **AL-33** — each button dials its own end of the delivery, and each logs its own `CalleeRole`.
    /// A direct cellular dial, not AL-48's Free-call / Normal-call chooser.
    func testEachCallButtonDialsItsOwnEndOfTheDelivery() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted)
        let model = makeModel()
        await model.refresh()

        await model.call(.sender)
        XCTAssertEqual(contact.dialled, [testSenderPhone])
        XCTAssertEqual(contact.roleCalls.last?.calleeRole, CalleeRole.sender)
        XCTAssertEqual(contact.roleCalls.last?.type, CallType.directDial)

        await model.call(.recipient)
        XCTAssertEqual(contact.dialled, [testSenderPhone, testRecipientPhone])
        XCTAssertEqual(contact.roleCalls.last?.calleeRole, CalleeRole.recipient)
        XCTAssertTrue(contact.calls.isEmpty, "the kind never decides who a delivery rings")
    }

    /// **The CallKit delta.** A `tel:` URL on iOS *places* the call, so dialling over one already up
    /// would hang it up. The refusal is copy, not a silent no-op — and the log is still written.
    func testADialRefusedBecauseACallIsUpBecomesCopy() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted)
        contact.dialSucceeds = false
        let model = makeModel()
        await model.refresh()

        await model.call(.recipient)

        XCTAssertEqual(model.state.errorKey, "ride_call_unavailable")
        XCTAssertEqual(contact.roleCalls.count, 1, "the log is still written; the dial is what failed")
    }

    /// A row with no number is disabled rather than hidden, and nothing is dialled through it: a missing
    /// row would make the sheet look like a delivery with one party, and there are always two.
    func testAPartyWithNoNumberDialsNothing() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted, senderPhone: nil)
        let model = makeModel()
        await model.refresh()

        XCTAssertNil(model.state.phone(of: .sender))
        await model.call(.sender)

        XCTAssertTrue(contact.dialled.isEmpty)
        XCTAssertTrue(contact.roleCalls.isEmpty)
    }

    /// US-12.8's alarm is a screen with a confirmation, not a button that fires.
    func testSosNavigatesRatherThanRaisingTheAlarmHere() async {
        deliveries.detailToReturn = packageRide(state: RideState.driverArrived)
        let model = makeModel()
        await model.refresh()

        model.openSos()
        XCTAssertTrue(model.state.isSosRequested)

        model.consume()
        XCTAssertFalse(model.state.isSosRequested)
    }

    // MARK: - What the sheets read

    /// MAP-10's circle is where the driver is being sent *now* — the sender's door until the parcel is
    /// aboard, the recipient's afterwards.
    func testTheGeofenceFollowsTheParcelRatherThanTheDriver() async {
        let model = makeModel()

        deliveries.detailToReturn = packageRide(state: RideState.accepted)
        await model.refresh()
        XCTAssertEqual(model.state.geofence?.lat, testHere.lat)

        deliveries.detailToReturn = packageRide(state: RideState.inProgress)
        await model.refresh()
        XCTAssertEqual(model.state.geofence?.lat, testThere.lat)
    }

    /// `Pickup` is driver-to-sender and `Drop` is sender-to-recipient: the second is the length of the
    /// *delivery*, which is what a driver deciding whether to take the job is reading.
    func testTheTwoLegsAreMeasuredFromDifferentPlaces() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted)
        let model = makeModel()
        await model.refresh()

        XCTAssertNil(model.state.pickupMetres, "no fix yet — 0.0 km reads as 'you are already there'")
        XCTAssertNotNil(model.state.dropMetres)

        model.start()
        location.emit(testFix(testHere))

        XCTAssertEqual(model.state.pickupMetres ?? -1, 0, accuracy: 1, "standing at the sender's door")
        XCTAssertEqual(model.state.dropMetres ?? 0, 1_200, accuracy: 100, "and the whole leg still to run")
        model.stop()
    }

    private func snapshot(_ state: RideState, _ version: Int32) -> RideStateSnapshot {
        RideStateSnapshot(state: state, version: version, offerExpiresAt: nil)
    }

    /// **There is no sender name on the wire** (Δ C037 added `recipientName` and no counterpart), so the
    /// sender's row is its role alone rather than a name borrowed from a field that means something else.
    func testThePartyLabelsNameOnlyWhatTheWireCarries() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted, recipientName: "Sunethra")
        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.label(of: .sender), "delivery_party_sender".localised)
        XCTAssertEqual(
            model.state.label(of: .recipient),
            "delivery_party_recipient".localised + MageRideSymbols.separator + "Sunethra"
        )

        deliveries.detailToReturn = packageRide(state: RideState.accepted, recipientName: nil)
        await model.refresh()
        XCTAssertEqual(model.state.label(of: .recipient), "delivery_party_recipient".localised)
    }

    /// `counterpartyPhone` is the recipient on a package ride (Δ C037), and is the fallback for a server
    /// that answers the older shape.
    func testTheRecipientFallsBackToTheCounterpartyNumber() async {
        deliveries.detailToReturn = packageRide(state: RideState.accepted, recipientPhone: nil)
        let model = makeModel()
        await model.refresh()

        XCTAssertNil(model.state.ride?.recipientPhone)
        XCTAssertEqual(model.state.phone(of: .recipient), testRecipientPhone)
    }
}
