import Combine
import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

// Cluster 8's rules, asserted with no gateway, no radio and no CallKit-capable process.
//
// Four suites, one per rule that would be expensive to discover on a device: the call's state machine
// and the *order* it tells CallKit about itself, AL-48's one fallback, the alarm's cancel window and
// D-34's after-the-fact link, and support's two halves — the accordion that must survive a failed
// ticket read, and the ticket that must survive a failed upload. A fifth pins the fences the C102
// prompt draws.

// MARK: - SCR-PI-028

/// The in-app VoIP call (US-6A.16, D-24, AL-48).
final class VoipCallModelTests: XCTestCase {

    private var rides: FakeRideRepository!
    private var contact: FakeRideContact!
    private var engine: FakeVoipEngine!
    private var session: FakeCallSession!

    @MainActor
    override func setUp() {
        super.setUp()
        rides = FakeRideRepository()
        contact = FakeRideContact()
        engine = FakeVoipEngine()
        session = FakeCallSession()
        rides.ride = RideFixtures.ride(
            state: RideState.inProgress,
            driver: RideFixtures.driver(),
            counterpartyPhone: RideFixtures.driverPhone
        )
        rides.callResponse = StartCallResponse(
            callId: RideFixtures.callId,
            callType: CallType.freeVoip,
            session: VoipSession(roomName: "ride_\(RideFixtures.rideId)", token: "jwt", wsUrl: "wss://sfu")
        )
    }

    @MainActor
    private func model() -> VoipCallModel {
        VoipCallModel(
            rideId: RideFixtures.rideId,
            rides: rides,
            contact: contact,
            engine: engine,
            session: session
        )
    }

    /// **One tap, one `comms.call_log` row.** SCR-PI-015a records the passenger's *choice* and
    /// navigates; this screen is the only caller of `POST /v1/calls/start` for a free call, and
    /// `.task` running twice after a scene change must not write a second row.
    @MainActor
    func testTheCallIsPlacedOnceAndTheRoomIsJoinedWithTheSessionItAnswered() async {
        let model = model()
        model.start()
        model.start()
        await settle()

        XCTAssertEqual(rides.calls.count, 1)
        XCTAssertEqual(rides.calls.first?.type, CallType.freeVoip)
        XCTAssertEqual(engine.joined.count, 1)
        XCTAssertEqual(engine.joined.first?.roomName, "ride_\(RideFixtures.rideId)")
        // The ride is read first and is what supplies the header and the fallback number.
        XCTAssertEqual(model.state.calleeName, "K. Fernando")
        XCTAssertEqual(model.state.counterpartyPhone, RideFixtures.driverPhone)
    }

    /// **A ride read that fails is not fatal.** A call the passenger can still place with a blank
    /// header beats a screen that refuses to try — but with no number there is no fallback to offer.
    @MainActor
    func testAFailedRideReadStillPlacesTheCallAndOffersNoDial() async {
        rides.rideFailure = FakeError.unreachable
        engine.linkOnJoin = .failed(.media)

        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(rides.calls.count, 1)
        XCTAssertNil(model.state.calleeName)
        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertFalse(model.state.canDialDirectly, "there is nobody left to reach")
    }

    /// **`POST /v1/calls/start` answering without a session is ``VoipFailure/signalling``**, and
    /// there is nothing to join.
    ///
    /// No outcome is reported either, and that is not an omission: the call never got a `callId`, so
    /// there is no `comms.call_log` row for one to be reported against.
    @MainActor
    func testASignallingFailureNeverJoinsARoomAndHasNoOutcomeToReport() async {
        rides.callFailure = FakeError.unreachable

        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertEqual(model.state.failure, .signalling)
        XCTAssertTrue(engine.joined.isEmpty)
        XCTAssertTrue(rides.outcomes.isEmpty)
    }

    /// **The engine this build binds fails on every handset, every time** — and that is exactly
    /// AL-48's condition, so the direct-dial prompt is the only outcome SCR-PI-028 reaches today.
    @MainActor
    func testTheAbsentEngineFailsWithNoMediaClientAndOffersTheDial() async {
        let absent = AbsentVoipEngine()
        let model = VoipCallModel(
            rideId: RideFixtures.rideId,
            rides: rides,
            contact: contact,
            engine: absent,
            session: session
        )
        model.start()
        await settle()

        XCTAssertEqual(model.state.failure, .noMediaClient)
        XCTAssertTrue(model.state.canDialDirectly)
        XCTAssertEqual(VoipCallScreen.failureKey(model.state.failure), "call_failed_unavailable")
        // The signalling half is real: the room was minted and `voip_failed` was reported.
        XCTAssertEqual(rides.calls.map(\.type), [CallType.freeVoip])
        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.voipFailed])
    }

    /// **CallKit is told about the call from the LINK, never from the tap.**
    ///
    /// A build with no media client reports **no call at all** rather than flashing one into the
    /// status bar and out again — which is what the empty log asserts.
    @MainActor
    func testAFailureBeforeConnectingReportsNoSystemCall() async {
        engine.linkOnJoin = .failed(.noMediaClient)

        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(session.log, [], "the system was never told about a call that did not happen")
    }

    /// The connected path, in order: connecting → connected → ended.
    @MainActor
    func testAConnectedCallReportsConnectingThenConnectedAndEndsCompleted() async {
        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(session.log, [.startedConnecting])
        XCTAssertEqual(session.connectingHandles, ["K. Fernando"])

        engine.report(.connected)
        XCTAssertEqual(model.state.stage, .connected)
        XCTAssertEqual(session.log, [.startedConnecting, .connected])

        model.hangUp()
        await settle()

        XCTAssertEqual(session.log, [.startedConnecting, .connected, .endedLocally])
        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.completed])
        XCTAssertTrue(model.state.isFinished)
    }

    /// A call abandoned while it was still ringing is the caller's own `cancelled`, not `completed`.
    @MainActor
    func testHangingUpWhileConnectingReportsCancelled() async {
        let model = model()
        model.start()
        await settle()

        model.hangUp()
        await settle()

        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.cancelled])
    }

    /// **A failure ends the reported call before the fallback is offered, and reports one outcome.**
    ///
    /// The ordering is load-bearing on this platform: a `tel:` URL *places* a call, so a dial taken
    /// while the system still believed a call was up would hang the new one straight back up. The
    /// second half — one outcome, not two — is what stops a hang-up after a failure writing
    /// `cancelled` over `voip_failed`.
    @MainActor
    func testAFailedCallEndsTheSystemCallFirstAndReportsExactlyOneOutcome() async {
        let model = model()
        model.start()
        await settle()

        engine.report(.connected)
        engine.report(.failed(.media))
        await settle()

        XCTAssertEqual(session.log, [.startedConnecting, .connected, .endedFailed])
        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.voipFailed])

        model.hangUp()
        await settle()

        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.voipFailed], "no second outcome")
    }

    /// **AL-48's fallback is a `direct_dial` row and then the dial**, in that order, so the platform
    /// can tell a fallback from a passenger who simply preferred to dial.
    @MainActor
    func testTheFallbackLogsADirectDialAgainstTheSameRideAndThenDials() async {
        engine.linkOnJoin = .failed(.noMediaClient)

        let model = model()
        model.start()
        await settle()

        model.dialDirectly()
        await settle()

        XCTAssertEqual(rides.calls.map(\.type), [CallType.freeVoip, CallType.directDial])
        XCTAssertEqual(rides.calls.map(\.rideId), [RideFixtures.rideId, RideFixtures.rideId])
        XCTAssertEqual(contact.dialled, [RideFixtures.driverPhone])
        XCTAssertTrue(model.state.isFinished)
        XCTAssertNil(model.state.errorKey)
    }

    /// **Δ iOS — a handset that claims no `tel:` URL is a state this screen draws.** Android's
    /// `ACTION_DIAL` only opens the dialler and cannot fail this way; here the dial is *placed*, and
    /// a simulator or an iPad answers `false`. The screen must not close on one.
    @MainActor
    func testARefusedDialBecomesCopyAndDoesNotCloseTheScreen() async {
        engine.linkOnJoin = .failed(.noMediaClient)
        contact.opens = false

        let model = model()
        model.start()
        await settle()

        model.dialDirectly()
        await settle()

        XCTAssertEqual(model.state.errorKey, "call_dial_refused")
        XCTAssertFalse(model.state.isFinished)

        model.consume()
        XCTAssertNil(model.state.errorKey)
    }

    /// **The outcome log is best-effort and never reaches the passenger.** A `404` on it says the
    /// call was already reported; a passenger trying to reach their driver must not see either.
    @MainActor
    func testAFailedOutcomeReportIsInvisible() async {
        rides.outcomeFailure = FakeError.unreachable
        engine.linkOnJoin = .failed(.media)

        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertNil(model.state.errorKey, "a log row is not something to tell a passenger about")
        XCTAssertEqual(rides.outcomes.count, 1, "it was attempted")
    }

    /// The two toggles move the engine, the screen and — for mute — the system. The speaker
    /// deliberately does not: CallKit has no `CX…Action` for an audio route.
    @MainActor
    func testTheTogglesMoveTheEngineAndOnlyMuteReachesTheSystem() async {
        let model = model()
        model.start()
        await settle()
        engine.report(.connected)

        model.toggleMute()
        model.toggleSpeaker()

        XCTAssertEqual(engine.muted, [true])
        XCTAssertEqual(engine.speaker, [true])
        XCTAssertEqual(session.muted, [true])
        XCTAssertTrue(model.state.isMuted)
        XCTAssertTrue(model.state.isSpeakerOn)
    }

    /// Control Centre's own switch arrives through the provider and moves the engine and the screen
    /// — it must not toggle a second time back to where it started.
    @MainActor
    func testTheSystemMuteSwitchIsFollowedRatherThanEchoed() async {
        let model = model()
        model.start()
        await settle()

        session.onSystemMute?(true)
        XCTAssertTrue(model.state.isMuted)
        XCTAssertEqual(engine.muted, [true])

        session.onSystemMute?(true)
        XCTAssertEqual(engine.muted, [true], "the same value twice is not two mutes")
    }

    /// The system's own **End** on the lock screen hangs up through the same method the red disc
    /// does, so one path reports one outcome.
    @MainActor
    func testTheSystemsOwnEndHangsUpThroughTheSameMethod() async {
        let model = model()
        model.start()
        await settle()

        session.onSystemEnd?()
        await settle()

        XCTAssertTrue(model.state.isFinished)
        XCTAssertEqual(rides.outcomes.map(\.outcome), [CallOutcome.cancelled])
    }

    /// The wireframe's `01:24`. Elapsed minutes and seconds, and they do not roll into hours.
    func testTheCallTimerIsMinutesAndSecondsAndNeverHours() {
        XCTAssertEqual(MoneyFormat.timer(seconds: 84), "01:24")
        XCTAssertEqual(MoneyFormat.timer(seconds: 0), "00:00")
        XCTAssertEqual(MoneyFormat.timer(seconds: 3_667), "61:07")
        XCTAssertEqual(MoneyFormat.timer(seconds: -5), "00:00", "a clock that ran backwards is not one")
    }
}

// MARK: - SCR-PI-029

/// The passenger SOS (US-12.1, AL-13, D-33, D-34).
final class SosModelTests: XCTestCase {

    private var safety: FakeSafetyRepository!
    private var contacts: FakeSosContacts!
    private var locations: FakePassengerLocationSource!

    @MainActor
    override func setUp() {
        super.setUp()
        safety = FakeSafetyRepository()
        contacts = FakeSosContacts()
        locations = FakePassengerLocationSource()
        contacts.stored = [
            AddressFixtures.contact(contactId: AddressFixtures.ammaId, isPrimary: true),
            AddressFixtures.contact(
                contactId: AddressFixtures.thathaId,
                name: "Thatha",
                phone: "+94770002222",
                isPrimary: false
            ),
        ]
    }

    @MainActor
    private func model() -> SosModel {
        SosModel(
            rideId: RideFixtures.rideId,
            safety: safety,
            contacts: contacts,
            locations: locations
        )
    }

    /// **`POST /v1/sos` has no positionless form**, so the disc reads `SOS` rather than a countdown
    /// until the handset has answered once — and a tap before then raises nothing.
    ///
    /// Recorded as a contract gap by C075, C084 and C093 and carried forward: BR-29.4 contemplates
    /// exactly this case for the *web* surface and the app-facing contract has no equivalent.
    @MainActor
    func testNothingIsArmedUntilThereIsAFixToSendWithIt() async {
        let model = model()
        model.start()
        await settle()

        XCTAssertTrue(model.state.isAwaitingPosition)
        XCTAssertEqual(model.state.secondsLeft, SosModel.countdownSeconds)

        model.raise()
        await settle()

        XCTAssertTrue(safety.raised.isEmpty)
        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertEqual(model.state.errorKey, "sos_no_position")
    }

    /// The window starts on the **first fix**, and a deliberate tap sends immediately rather than
    /// waiting out the timer it interrupted.
    @MainActor
    func testTheFirstFixArmsTheWindowAndATapSendsTheLastKnownPosition() async {
        let model = model()
        model.start()
        await settle()

        locations.emit(SafetyFixtures.fix)
        await settle()
        XCTAssertFalse(model.state.isAwaitingPosition)

        model.raise()
        await settle()

        XCTAssertEqual(safety.raised.count, 1)
        XCTAssertEqual(safety.raised.first?.rideId, RideFixtures.rideId)
        XCTAssertEqual(safety.raised.first?.lat, SafetyFixtures.fix.lat)
        XCTAssertEqual(safety.raised.first?.lng, SafetyFixtures.fix.lng)
        XCTAssertEqual(model.state.stage, .dispatched)
        XCTAssertTrue(model.state.isRaised)
    }

    /// **One emergency, one row on the operator's feed.** A second tap while the request is in
    /// flight or after it has been answered does nothing, and the state is sticky.
    @MainActor
    func testASecondTapNeverRaisesASecondAlarm() async {
        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()

        model.raise()
        model.raise()
        await settle()
        model.raise()
        await settle()

        XCTAssertEqual(safety.raised.count, 1)
    }

    /// **D-34's link is minted after the alarm and is allowed to fail.** Putting
    /// `POST /v1/trip-share/{tripId}` in front of `POST /v1/sos` would spend the five-second budget
    /// on a URL; a `409 ride-terminal` leaves the alarm exactly where it is.
    @MainActor
    func testTheShareLinkIsMintedAfterTheAlarmAndItsFailureIsSwallowed() async {
        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()

        model.raise()
        await settle()

        XCTAssertEqual(safety.shared, [RideFixtures.rideId])
        XCTAssertEqual(model.state.shareLink, SafetyFixtures.shareLink.url)

        // And again, with the link refused.
        safety.shareFailure = FakeError.unreachable
        let second = self.model()
        second.start()
        locations.emit(SafetyFixtures.fix)
        await settle()
        second.raise()
        await settle()

        XCTAssertEqual(second.state.stage, .dispatched, "an alarm that went out is still an alarm")
        XCTAssertNil(second.state.shareLink)
        XCTAssertNil(second.state.errorKey)
    }

    /// **A failed SMS is not a failed SOS.** The alert is recorded and is on the admin live feed
    /// either way; the pill says which leg did not manage it and does not colour like an error.
    @MainActor
    func testAFailedSmsLegLeavesTheAlarmDispatchedAndIsNotAnErrorTone() async {
        safety.dispatched = SafetyFixtures.dispatched(smsStatus: SosSmsStatus.failed)

        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()
        model.raise()
        await settle()

        XCTAssertEqual(model.state.stage, .dispatched)
        XCTAssertEqual(model.state.smsStatus, SosSmsStatus.failed)
        XCTAssertNil(model.state.errorKey)
        XCTAssertEqual(SosScreen.smsLabelKey(SosSmsStatus.failed), "sos_sms_failed")
        XCTAssertEqual(SosScreen.smsTone(SosSmsStatus.failed), .warning)
        XCTAssertEqual(SosScreen.smsTone(SosSmsStatus.dispatched), .ok)
    }

    /// **Only the primary contact is texted, so only the primary wears the pill.** iam-svc
    /// denormalises exactly one onto `iam.users.emergency_contact_name/phone` because a join does not
    /// fit p99 ≤ 5 s; `Sent` against three names would be a fan-out the platform does not do.
    @MainActor
    func testTheWholeListIsDrawnAndThePrimaryIsTheOneTheSmsReaches() async {
        let model = model()
        model.start()
        await settle()

        XCTAssertEqual(model.state.contacts.count, 2)
        XCTAssertEqual(model.state.primaryContact?.contactId, AddressFixtures.ammaId)
        XCTAssertFalse(model.state.warnsNoContact)
    }

    /// **AL-13's warning is shown before the alarm, not instead of it.** An empty list is what makes
    /// `POST /v1/sos` answer `400 no-emergency-contact`, and the disc still tries — refusing locally
    /// would be this app deciding an outcome the platform owns.
    @MainActor
    func testAnEmptyContactListWarnsBeforeTheTapAndStillSends() async {
        contacts.stored = []

        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()

        XCTAssertTrue(model.state.warnsNoContact)

        model.raise()
        await settle()

        XCTAssertEqual(safety.raised.count, 1, "the setting can be off; the platform decides")
    }

    /// A request that never reached safety-svc offers a retry, and the retry re-arms the disc.
    @MainActor
    func testARequestThatNeverLeftTheHandsetCanBeRetried() async {
        safety.sosFailure = FakeError.unreachable

        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()
        model.raise()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertEqual(model.state.errorKey, "error_generic")

        model.retry()
        XCTAssertEqual(model.state.stage, .armed)
        XCTAssertEqual(model.state.secondsLeft, SosModel.countdownSeconds)
        XCTAssertNil(model.state.errorKey)

        model.raise()
        await settle()

        XCTAssertEqual(safety.raised.count, 2)
        XCTAssertEqual(model.state.stage, .dispatched)
    }

    /// **`stop()` is not a cancel of the alarm.** `POST /v1/sos` is not revocable, so a screen going
    /// away must not be able to un-raise one — it stops the countdown and the fix subscription only.
    @MainActor
    func testLeavingTheScreenStopsTheCountdownAndNotTheAlarm() async {
        let model = model()
        model.start()
        locations.emit(SafetyFixtures.fix)
        await settle()

        model.raise()
        model.stop()
        await settle()

        XCTAssertEqual(safety.raised.count, 1)
        XCTAssertEqual(model.state.stage, .dispatched)
    }

    /// Three seconds, and it is the same number on all four apps.
    ///
    /// D5' §14.3 fixes the **dispatch** budget (p99 ≤ 5 s) and says nothing about a confirmation, so
    /// this is not a spec number — but one platform must not have two answers to *"how long do I have
    /// to cancel"*, which is what makes the constant worth pinning.
    func testTheCancelWindowIsThreeSeconds() {
        XCTAssertEqual(SosModel.countdownSeconds, 3)
    }

    /// `SOS` is a distress signal, not a sentence, and is deliberately not in the `.strings` files.
    func testTheDiscsWordIsDataRatherThanCopy() {
        XCTAssertEqual(SosLabels.sos, "SOS")
    }
}

/// D-26 — a failure is copy this app resolved from a kebab code, never a `ProblemDetails` string.
final class SafetyErrorsTests: XCTestCase {

    /// **Every code `safety.yaml` declares on this cluster's two operations has copy**, and every one
    /// of them is a key the three `.strings` files carry — `LocalizationTests` checks the second half.
    ///
    /// A `MageRideError` cannot be constructed from Swift without the Kotlin initialiser (the C095
    /// finding), so what is asserted here is the *table*: the keys it can produce, against the ones
    /// declared. The wiring from a thrown error to it is covered by ``SosModelTests`` above.
    func testTheErrorTableCoversTheCodesThisScreenCanSee() {
        let keys = [
            "error_no_emergency_contact", "error_ride_ended", "error_not_your_ride",
            "error_ride_not_found", "error_validation_failed", "error_dependency_unavailable",
            "error_offline", "error_generic", "sos_no_position",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
    }

    /// **`no-emergency-contact` is the one code on this surface a passenger can fix themselves**, so
    /// it must not collapse into the generic sentence — the fix is SCR-PI-027b, and the copy has to
    /// be able to say so.
    func testTheSetupFailureDoesNotReadAsAGenericOne() {
        XCTAssertNotEqual("error_no_emergency_contact".localised, "error_generic".localised)
        XCTAssertEqual(SafetyErrors.messageKey(for: FakeError.unreachable), "error_generic")
    }
}

/// The same, over `support.yaml`'s small surface.
final class SupportErrorsTests: XCTestCase {

    func testTheErrorTableCoversTheCodesThisScreenCanSee() {
        let keys = [
            "error_screenshot_too_large", "error_ticket_not_found", "error_validation_failed",
            "error_dependency_unavailable", "error_offline", "error_generic",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
        XCTAssertEqual(SupportErrors.messageKey(for: FakeError.unreachable), "error_generic")
    }

    /// **Separate tables, because they are separate contracts.** One `switch` over the whole platform
    /// is a function nobody can check — the argument every one of the seven makes, and the reason a
    /// `413` on a screenshot and a `413` on a Mode B transfer slip have different sentences.
    func testTheScreenshotCeilingIsNotTheTransferSlipsSentence() {
        XCTAssertNotEqual("error_screenshot_too_large".localised, "error_slip_too_large".localised)
    }
}

// MARK: - SCR-PI-030 / 030a

/// Support, the FAQ accordion and the raise-ticket sheet (US-16.1, US-16.2).
final class SupportModelTests: XCTestCase {

    private var support: FakeSupportRepository!
    private var sessions: FakePassengerSessions!
    private var preferences: FakeAppPreferences!

    @MainActor
    override func setUp() {
        super.setUp()
        support = FakeSupportRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = RideFixtures.passengerId
        preferences = FakeAppPreferences()
        support.articles = SupportFixtures.summaries()
        support.storedTickets = [SupportFixtures.ticket()]
    }

    @MainActor
    private func model() -> SupportModel {
        SupportModel(support: support, sessions: sessions, preferences: preferences)
    }

    /// **AL-26 — the FAQ is asked for in the language the app is DRAWING in**, not the profile's.
    ///
    /// `apps/driver-ios` sends `nil` and lets support-svc use the profile, which is right there; here
    /// the language is a device-first answer given on SCR-PI-002 before there is a session, and the
    /// server write is allowed to lag (`languagePendingSync`). A passenger reading a Sinhala app
    /// whose profile had not caught up would get English articles inside it.
    @MainActor
    func testTheFaqIsAskedForInTheLanguageTheAppIsDrawingIn() async {
        preferences.language = Language.si

        let model = model()
        await model.refresh()

        XCTAssertEqual(support.faqLanguages, [Language.si])

        // And before SCR-PI-002 has been answered, `nil` still means "use the profile's".
        preferences.language = nil
        let fresh = self.model()
        await fresh.refresh()
        XCTAssertEqual(support.faqLanguages.count, 2)
        XCTAssertNil(support.faqLanguages[1])
    }

    /// An article is fetched in the same language, for the same reason.
    @MainActor
    func testAnOpenedArticleIsFetchedInTheSameLanguage() async {
        preferences.language = Language.ta

        let model = model()
        await model.refresh()
        await model.toggleArticle(articleId: SupportFixtures.receiptArticleId)

        XCTAssertEqual(support.articleReads.map(\.articleId), [SupportFixtures.receiptArticleId])
        XCTAssertEqual(support.articleReads.first?.language, Language.ta)
    }

    /// **A ticket list that could not be read leaves the FAQ up.** *"Search help"* is the half a
    /// passenger with a failing session can still use, which is why the articles are committed first.
    @MainActor
    func testAFailedTicketReadLeavesTheArticlesOnScreen() async {
        support.ticketsFailure = FakeError.unreachable

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.articles.count, 2)
        XCTAssertTrue(model.state.tickets.isEmpty)
        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)
    }

    /// A signed-out passenger has no ticket list and no failure — the FAQ simply works.
    @MainActor
    func testASignedOutPassengerStillGetsTheFaq() async {
        sessions.userId = nil

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.articles.count, 2)
        XCTAssertTrue(support.ticketReads.isEmpty)
        XCTAssertNil(model.state.errorKey)
    }

    /// **The accordion opens one row at a time**, and tapping the open row closes it. Two open bodies
    /// would push *"Your tickets"* off a 5.4" screen.
    @MainActor
    func testTheAccordionKeepsOneRowOpen() async {
        let model = model()
        await model.refresh()

        await model.toggleArticle(articleId: SupportFixtures.receiptArticleId)
        XCTAssertEqual(model.state.expandedArticleId, SupportFixtures.receiptArticleId)
        XCTAssertNotNil(model.state.expandedArticle)

        await model.toggleArticle(articleId: SupportFixtures.paymentArticleId)
        XCTAssertEqual(model.state.expandedArticleId, SupportFixtures.paymentArticleId)

        await model.toggleArticle(articleId: SupportFixtures.paymentArticleId)
        XCTAssertNil(model.state.expandedArticleId)
        XCTAssertNil(model.state.expandedArticle, "a closed row keeps no body")
    }

    /// **The search filters on the device and sends nothing.** `GET /v1/support/faq` takes a category
    /// and no query string, so a request per keystroke would buy nothing at all.
    @MainActor
    func testTheSearchFiltersLocallyAndSendsNoRequest() async {
        let model = model()
        await model.refresh()
        let readsBefore = support.faqLanguages.count

        model.onSearchChange("receipt")

        XCTAssertEqual(model.state.visibleArticles.map(\.articleId), [SupportFixtures.receiptArticleId])
        XCTAssertEqual(support.faqLanguages.count, readsBefore, "no request went out")

        model.onSearchChange("   ")
        XCTAssertEqual(model.state.visibleArticles.count, 2, "whitespace is not a search")
    }

    /// **Every ticket this app raises is `general`.** `daily_fee_refund` is the *driver's* fee
    /// (US-9.23), so no passenger-facing category derives `TicketQueue.finance` and SCR-PI-030 has no
    /// quick action.
    @MainActor
    func testEveryTicketIsGeneralAndTheNewRowIsPrepended() async {
        let model = model()
        await model.refresh()

        model.openTicketSheet()
        model.onDescriptionChange("  Charged twice for one trip  ")
        XCTAssertTrue(model.state.canSubmit)

        await model.submit()

        XCTAssertEqual(support.raisedTickets.map(\.category), [SupportCategories.general])
        XCTAssertEqual(support.raisedTickets.first?.description, "  Charged twice for one trip  ")
        XCTAssertEqual(model.state.tickets.first?.ticketId, SupportFixtures.raisedTicketId)
        XCTAssertEqual(model.state.tickets.count, 2, "prepended, not re-read")
        XCTAssertEqual(model.state.raisedTicketId, SupportFixtures.raisedTicketId)
        XCTAssertNil(model.state.sheet)
    }

    /// A ticket with nothing written on it is nothing for support to act on.
    @MainActor
    func testSubmitRefusesABlankDescription() async {
        let model = model()
        await model.refresh()
        model.openTicketSheet()
        model.onDescriptionChange("   ")

        XCTAssertFalse(model.state.canSubmit)
        await model.submit()

        XCTAssertTrue(support.raisedTickets.isEmpty)
    }

    /// **The screenshot is uploaded by Submit, then linked** — two calls, because that is the
    /// contract's shape: `POST /v1/support/tickets` takes an already-uploaded id.
    @MainActor
    func testTheScreenshotIsUploadedBySubmitAndLinkedToTheTicket() async {
        let model = model()
        await model.refresh()
        model.openTicketSheet()
        model.onDescriptionChange("The driver took a longer route")
        model.onScreenshotPicked(fileName: "shot.jpg", data: Data(repeating: 0x1, count: 128))

        XCTAssertTrue(support.uploads.isEmpty, "the picker sends nothing")

        await model.submit()

        XCTAssertEqual(support.uploads.map(\.byteCount), [128])
        XCTAssertEqual(support.raisedTickets.first?.screenshotFileId, SupportFixtures.fileId)
    }

    /// **A failed upload never costs the passenger their ticket.** What they wrote is the part
    /// support acts on; the attachment is simply absent.
    @MainActor
    func testAFailedUploadStillRaisesTheTicketWithoutTheAttachment() async {
        support.uploadFailure = FakeError.unreachable

        let model = model()
        await model.refresh()
        model.openTicketSheet()
        model.onDescriptionChange("Charged twice")
        model.onScreenshotPicked(fileName: "shot.jpg", data: Data(repeating: 0x1, count: 64))

        await model.submit()

        XCTAssertEqual(support.raisedTickets.count, 1)
        XCTAssertNil(support.raisedTickets.first?.screenshotFileId)
        XCTAssertNil(model.state.errorKey)
    }

    /// **The trip picker is C099's history read, and it happens once per sheet opening.** D2'
    /// §SCR-PI-030a names `GET /v1/rides` and this app already has that read.
    @MainActor
    func testTheTripPickerReadsTheHistoryOnceAndTheSelectionIsOptional() async {
        support.storedTrips = [HistoryFixtures.row()]

        let model = model()
        await model.refresh()

        model.openTicketSheet()
        await settle()
        model.openTicketSheet()
        await settle()

        XCTAssertEqual(support.tripReads, 1)
        XCTAssertEqual(model.state.trips.count, 1)

        model.onDescriptionChange("Lost my bag")
        model.onTripSelected(HistoryFixtures.rideId)
        await model.submit()
        XCTAssertEqual(support.raisedTickets.first?.tripId, HistoryFixtures.rideId)
    }

    /// Opening a thread that cannot be read takes the sheet back down rather than leaving a spinner.
    @MainActor
    func testAThreadThatCannotBeReadClosesItsSheet() async {
        support.detailFailure = FakeError.unreachable

        let model = model()
        await model.refresh()
        await model.openTicket(ticketId: SupportFixtures.ticketId)

        XCTAssertNil(model.state.sheet)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }
}

// MARK: - The copy table

/// ``SupportLabels`` — the one place a support wire value becomes copy.
final class SupportLabelTests: XCTestCase {

    /// `assigned` is in the enum and is **never returned to a user**: who inside MageRide is handling
    /// a complaint is not the complainant's business, so the thread skips it rather than printing an
    /// empty row.
    func testAnAssignedEventHasNoCopyAndEveryOtherKindDoes() {
        XCTAssertNil(SupportLabels.eventKey(TicketEventKind.assigned))
        for kind in [
            TicketEventKind.opened,
            TicketEventKind.responded,
            TicketEventKind.resolved,
            TicketEventKind.reopened,
        ] {
            XCTAssertNotNil(SupportLabels.eventKey(kind), "\(kind.name) has no copy")
        }
    }

    /// Every `TicketStatus` has copy and a tone; nothing reaches a default that says *"Resolved"*
    /// about an open ticket.
    func testEveryTicketStatusHasItsOwnCopyAndTone() {
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.open), "support_status_open")
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.inProgress), "support_status_in_progress")
        XCTAssertEqual(SupportLabels.statusKey(TicketStatus.resolved), "support_status_resolved")
        XCTAssertEqual(SupportLabels.tone(TicketStatus.open), .warning)
        XCTAssertEqual(SupportLabels.tone(TicketStatus.inProgress), .info)
        XCTAssertEqual(SupportLabels.tone(TicketStatus.resolved), .ok)
    }

    /// **A category is a free-text server key**, so an unknown one is made legible rather than
    /// collapsed into *"Support request"* — a passenger looking at their own list has to tell two
    /// tickets apart.
    func testAnUnknownCategoryIsMadeLegibleRatherThanRenamed() {
        XCTAssertEqual(SupportLabels.category("driver_qr_dispute"), "Driver qr dispute")
        XCTAssertEqual(SupportLabels.category(""), "")
    }

    /// **The trip row is the day and the route**, because this platform mints no `PAX-90431-0617`.
    /// The day is Colombo's — a passenger naming yesterday's trip must name the day support sees.
    func testTheTripRowIsTheColomboDayAndTheRoute() {
        let label = SupportLabels.trip(HistoryFixtures.row())
        XCTAssertTrue(label.contains(MageRideSymbols.routeArrow), "the route is drawn")
        XCTAssertTrue(label.contains(MageRideSymbols.separator))
        XCTAssertFalse(label.contains(HistoryFixtures.rideId), "a ULID is not a trip number")
    }
}

// MARK: - The fences

/// The fences the C102 prompt draws, enforced rather than remembered.
///
/// > *"Parity-fenced to C084. CallKit for VoIP; direct-dial fallback on failure."*
final class CommsFenceTests: XCTestCase {

    /// **The masked-number PSTN bridge and D-25's masked-SMS relay were both withdrawn.** There is
    /// exactly one fallback from a failed VoIP call and it is a `tel:` dial of the real number the
    /// ride already carries post-accept (US-26.4).
    ///
    /// Written out rather than mapped off `CallType.entries` — that property is Kotlin's and does not
    /// cross the bridge as anything a Swift `for` can use. `ordinal` is what pins *"and there is no
    /// third"*: a case added to `:shared` shifts one of these two and fails here.
    func testThereAreExactlyTwoCallTypesAndNeitherIsAMaskedRelay() {
        XCTAssertEqual(CallType.freeVoip.name, "FREE_VOIP")
        XCTAssertEqual(CallType.directDial.name, "DIRECT_DIAL")
        XCTAssertEqual([CallType.freeVoip.ordinal, CallType.directDial.ordinal].sorted(), [0, 1])
    }

    /// **`NSMicrophoneUsageDescription` is absent, and that is the honest state of this build.**
    ///
    /// `Info.plist`'s own header keeps a purpose string out until there is code behind it — the same
    /// rule `apps/passenger-android`'s `ManifestTest` holds about `RECORD_AUDIO`. The day
    /// ``AbsentVoipEngine`` is replaced with a real WebRTC client this assertion fails and asks for
    /// the string, which is exactly when it should be added.
    func testNoMicrophonePurposeStringIsDeclaredWhileTheEngineCarriesNoMedia() {
        XCTAssertNil(
            Bundle.main.object(forInfoDictionaryKey: "NSMicrophoneUsageDescription"),
            "a purpose string with no code behind it is a permission prompt nobody can explain"
        )
    }

    /// **C102 took the last three placeholders, so every route now draws a real screen.**
    ///
    /// The two `route_placeholder_*` keys went with them; a key that came back would be copy for a
    /// state the app can no longer be in.
    func testEveryRouteIsRegisteredAndThePlaceholderCopyIsGone() {
        let bundle = Bundle(for: MageRideBundleToken.self)
        for key in ["route_placeholder_title", "route_placeholder_body"] {
            XCTAssertEqual(
                NSLocalizedString(key, bundle: bundle, comment: ""),
                key,
                "\(key) is back — a route with nothing behind it is what it describes"
            )
        }
    }

    /// **Both takeovers are presented over the whole app, tab bar included**, and neither is a
    /// pushed destination: a passenger on an alarm screen must not be one tap from their trip
    /// history, and neither cell draws a tab bar.
    func testTheCallAndTheAlarmAreFullScreenTakeoversAndSupportIsATab() {
        XCTAssertTrue(PassengerRoute.voipCall(rideId: RideFixtures.rideId).isFullScreenTakeover)
        XCTAssertTrue(PassengerRoute.sos(rideId: RideFixtures.rideId).isFullScreenTakeover)
        XCTAssertFalse(PassengerRoute.support.isFullScreenTakeover)
        XCTAssertEqual(PassengerRoute.support.tab, .support)
        XCTAssertEqual(PassengerMenuDestination.support.route, .support)
    }
}

// MARK: -

/// Lets the `Task`s a model launched run to completion.
///
/// The models here start their work in a `Task` rather than in `init` (this target's rule — a
/// `@StateObject` is constructed eagerly), so an assertion straight after `start()` would read the
/// state before the read landed. `Task.yield()` in a loop drains the main actor's queue without a
/// wall-clock sleep, which is what keeps this suite fast enough to run on every build.
@MainActor
private func settle(_ turns: Int = 12) async {
    for _ in 0..<turns { await Task.yield() }
}
