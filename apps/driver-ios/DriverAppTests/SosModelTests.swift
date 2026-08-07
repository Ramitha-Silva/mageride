import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-032 · the driver SOS** — D-33's budget, AL-13's contact, and the position the contract
/// gives the alarm no way to do without.
@MainActor
final class SosModelTests: XCTestCase {

    private var contact = FakeRideContact()
    private var profiles = FakeProfileRepository()
    private var location = FakeDriverLocationSource()

    override func setUp() {
        super.setUp()
        contact = FakeRideContact()
        profiles = FakeProfileRepository()
        location = FakeDriverLocationSource()
    }

    private func makeModel() -> SosModel {
        SosModel(rideId: testRideId, contact: contact, profiles: profiles, location: location)
    }

    private func settle() async {
        for _ in 0..<8 { await Task.yield() }
    }

    // MARK: - The position the alarm cannot do without

    /// **`POST /v1/sos` has no positionless form.** `TriggerSosRequest.lat`/`.lng` are required, so
    /// there is no request to make until the handset has answered once — and the disc reads `SOS`
    /// rather than a countdown, because there is nothing to count down to.
    func testTheAlarmWaitsForAFixBeforeItArms() async {
        let model = makeModel()
        model.start()
        await settle()

        XCTAssertTrue(model.state.isAwaitingPosition)
        XCTAssertEqual(model.state.stage, .armed)
    }

    /// A tap with no fix is refused with its own copy rather than sent with a coordinate of nothing.
    func testTappingWithNoFixFailsWithItsOwnCopyAndSendsNothing() async {
        let model = makeModel()
        model.start()
        await settle()

        model.raise()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertEqual(model.state.errorKey, "sos_no_position")
        XCTAssertTrue(contact.alarms.isEmpty)
    }

    /// The first emission is the **last known** fix, which is what makes the wait milliseconds on any
    /// handset that has ever had one. Waiting for a fresh lock inside D-33's five-second budget is
    /// how an alarm arrives after the moment it was needed.
    func testTheFirstFixArmsTheDiscAndIsWhatTheAlarmCarries() async {
        let model = makeModel()
        model.start()
        await settle()

        location.emit(testFix())
        await settle()

        XCTAssertFalse(model.state.isAwaitingPosition)

        model.raise()
        await settle()

        XCTAssertEqual(contact.alarms.count, 1)
        XCTAssertEqual(contact.alarms.first?.rideId, testRideId)
        XCTAssertEqual(contact.alarms.first?.at.lat, testHere.lat)
        XCTAssertEqual(contact.alarms.first?.at.lng, testHere.lng)
    }

    // MARK: - One alarm per trip

    /// A second tap while the request is in flight or after it has been answered does nothing: there
    /// is one alarm per trip, and a second `POST` would be a second row on the operator's feed for
    /// the same emergency.
    func testASecondTapDoesNotRaiseASecondAlarm() async {
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        model.raise()
        await settle()
        model.raise()
        await settle()

        XCTAssertEqual(contact.alarms.count, 1)
    }

    /// Sticky. Nothing takes the screen back out of the dispatched state.
    func testTheDispatchedStateIsSticky() async {
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        model.raise()
        await settle()

        XCTAssertTrue(model.state.isRaised)
        model.retry()
        XCTAssertTrue(model.state.isRaised, "retry is only reachable from a failure")
    }

    // MARK: - D-33's two legs

    /// **A failed SMS is not a failed SOS.** `SosSmsStatus.failed` means the alert **is** recorded and
    /// **is** on the admin live feed and the SMS leg did not manage it — so the screen stays
    /// dispatched and the pill says which leg failed, in the pending tone rather than the error one.
    func testAFailedSmsLegLeavesTheAlarmDispatched() async {
        contact.nextDispatch = SosDispatched(sosId: "01JSOS0", dispatchedAt: nil, smsStatus: SosSmsStatus.failed)
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        model.raise()
        await settle()

        XCTAssertEqual(model.state.stage, .dispatched)
        XCTAssertEqual(model.state.smsStatus, SosSmsStatus.failed)
        XCTAssertEqual(SosScreen.smsLabelKey(SosSmsStatus.failed), "sos_sms_failed")
        XCTAssertEqual(SosScreen.smsTone(SosSmsStatus.failed), .pending, "not an error tone — the alert was raised")
    }

    func testOnlyADispatchedSmsWearsTheDoneTone() {
        XCTAssertEqual(SosScreen.smsTone(SosSmsStatus.dispatched), .done)
        XCTAssertEqual(SosScreen.smsTone(SosSmsStatus.noContact), .pending)
        XCTAssertEqual(SosScreen.smsLabelKey(SosSmsStatus.dispatched), "sos_sms_sent")
        XCTAssertEqual(SosScreen.smsLabelKey(SosSmsStatus.noContact), "sos_sms_no_contact")
    }

    /// Only a request that never reached safety-svc is a failure, and that one offers a retry.
    func testARequestThatNeverLeftTheHandsetFailsAndCanBeRetried() async {
        contact.nextSosFailure = CancellationError()
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        model.raise()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertNotNil(model.state.errorKey)

        model.retry()

        XCTAssertEqual(model.state.stage, .armed)
        XCTAssertEqual(model.state.secondsLeft, SosModel.countdownSeconds)
        XCTAssertNil(model.state.errorKey)
    }

    // MARK: - AL-13 · the contact

    /// *"exactly one per account that has any"* — the primary is what D-33's fast path denormalises
    /// onto `iam.users`, so it is the one the SMS will actually reach.
    func testThePrimaryContactIsPreferredOverTheFirstOneListed() async {
        profiles.contacts = [
            emergencyContact(contactId: "c1", isPrimary: false, name: "Sunil"),
            emergencyContact(contactId: "c2", isPrimary: true, name: "Amma"),
        ]
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertEqual(model.state.contact?.name, "Amma")
    }

    /// A driver with no contact on file is **told**, not refused: `POST /v1/sos` still records the
    /// event and still raises the admin live feed, and safety-svc answers `NoContact` for the leg
    /// that has nowhere to go.
    func testADriverWithNoContactIsWarnedAndStillRaisesTheAlarm() async {
        profiles.contacts = []
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        XCTAssertTrue(model.state.warnsNoContact)

        model.raise()
        await settle()

        XCTAssertEqual(contact.alarms.count, 1, "the half that works is not taken away")
    }

    /// The warning is drawn only once the read has answered — a blank card during the read would
    /// tell a driver who has a contact that they do not.
    func testTheWarningIsNotDrawnBeforeTheContactReadAnswers() {
        let model = makeModel()
        XCTAssertFalse(model.state.warnsNoContact)
    }

    // MARK: - The cancel window

    /// **Three seconds, and it is not a spec number.** D5' §14.3 fixes the *dispatch* budget
    /// (p99 ≤ 5 s) and says nothing about a confirmation; three is what is left of a five-second
    /// sense of urgency once a mis-tap on the largest control in the app has to be recoverable.
    func testTheCancelWindowIsThreeSecondsAndMatchesTheAndroidTwin() {
        XCTAssertEqual(SosModel.countdownSeconds, 3)
    }

    /// **Cancel** stops the auto-send, and a screen that has gone must not still be able to raise one.
    func testCancellingTheCountdownStopsTheAutoSend() async {
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()

        model.cancelCountdown()
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(model.state.stage, .armed)
        XCTAssertTrue(contact.alarms.isEmpty)
    }

    /// **`stop()` is not a cancel of the alarm.** A request already in flight is not revocable, and a
    /// screen going away must not be able to un-raise one.
    func testStoppingTheScreenDoesNotUnraiseAnAlarmAlreadySent() async {
        let model = makeModel()
        model.start()
        await settle()
        location.emit(testFix())
        await settle()
        model.raise()
        await settle()

        model.stop()

        XCTAssertTrue(model.state.isRaised)
        XCTAssertEqual(contact.alarms.count, 1)
        XCTAssertEqual(location.stopCount, 1, "the GNSS subscription is a screen's and does go")
    }

    /// `SOS` is an international distress signal and is a Swift constant, not copy — the same rule
    /// `Rs` and `+94` follow, and the reason `LocalizationTests` would (correctly) fail on it.
    func testTheDistressSignalIsNotATranslatableString() {
        XCTAssertEqual(SosLabels.sos, "SOS")
    }
}
