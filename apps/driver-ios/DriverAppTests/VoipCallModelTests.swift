import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-031 · the in-app VoIP call** — P-05's callee, AL-48's fallback, and the CallKit ordering
/// the fallback depends on.
@MainActor
final class VoipCallModelTests: XCTestCase {

    private var rides = FakeActiveRideRepository()
    private var contact = FakeRideContact()
    private var engine = FakeVoipEngine()
    private var session = FakeCallSession()

    override func setUp() {
        super.setUp()
        rides = FakeActiveRideRepository()
        contact = FakeRideContact()
        engine = FakeVoipEngine()
        session = FakeCallSession()
        contact.nextCall = startCallResponse()
    }

    private func makeModel() -> VoipCallModel {
        VoipCallModel(rideId: testRideId, rides: rides, contact: contact, engine: engine, session: session)
    }

    /// Runs the model's own `Task`s to completion. `start()` is fire-and-forget by design — the
    /// screen calls it from `.task` — so a test has to let the loop turn.
    private func settle() async {
        for _ in 0..<8 { await Task.yield() }
    }

    // MARK: - P-05 · who is called

    /// **The driver calls the RIDER, never the booker.** On a proxy booking the person in the vehicle
    /// is not the person who paid, and `CalleeRole.passenger` is what `comms.call_log` records.
    func testTheCallIsAlwaysLoggedAgainstThePassengerRole() async {
        let model = makeModel()
        model.start()
        await settle()

        XCTAssertEqual(contact.roleCalls.map(\.calleeRole), [CalleeRole.passenger])
        XCTAssertEqual(contact.roleCalls.map(\.type), [CallType.freeVoip])
        XCTAssertTrue(contact.calls.isEmpty, "the kind-based overload cannot answer P-05 and is not used")
    }

    /// The header comes from the ride, and its failure is not fatal: a call the driver can still
    /// place with a blank header beats a screen that refuses to try.
    func testTheRideIsReadForTheNameAndTheFallbackNumber() async {
        rides.detailToReturn = rideDetail(counterpartyPhone: "+94770000111")
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertEqual(model.state.calleeName, "Nimal")
        XCTAssertEqual(model.state.counterpartyPhone, "+94770000111")
    }

    func testAFailedRideReadStillPlacesTheCall() async {
        rides.nextFailure = CancellationError()
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertNil(model.state.calleeName)
        XCTAssertEqual(contact.roleCalls.count, 1, "the call is still started")
    }

    // MARK: - The link, the timer and CallKit

    /// `POST /v1/calls/start` answering without a session is ``VoipFailure/signalling``: there is
    /// nothing to join.
    func testAResponseWithNoSessionIsASignallingFailure() async {
        contact.nextCall = startCallResponse(session: nil)
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertEqual(model.state.failure, .signalling)
        XCTAssertTrue(engine.joined.isEmpty)
    }

    /// The outcome log is the only way voip-svc sees a call that never connected.
    func testAFailureReportsVoipFailedAgainstTheCallItOpened() async {
        contact.nextCall = startCallResponse(session: nil)
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertEqual(contact.outcomes.map(\.outcome), [CallOutcome.voipFailed])
        XCTAssertEqual(contact.outcomes.first?.callId, testCallId)
    }

    /// **The system learns about the call from the LINK, never from the tap.** A build whose engine
    /// fails before it reports connecting reports no CallKit call at all — which is what stops a
    /// system call flashing into the status bar and out again for a call that never happened.
    func testNoCallIsReportedToCallKitWhenTheEngineNeverConnects() async {
        engine.linkOnJoin = .failed(.noMediaClient)
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertTrue(session.connectingHandles.isEmpty)
        XCTAssertEqual(session.connectedCount, 0)
        XCTAssertEqual(model.state.failure, .noMediaClient)
    }

    func testConnectingReportsAnOutgoingCallAndConnectedStartsTheSystemTimer() async {
        let model = makeModel()
        model.start()
        await settle()

        engine.emit(.connecting)
        XCTAssertEqual(session.connectingHandles, ["Nimal"], "the generic handle is the name, never the number")

        engine.emit(.connected)
        XCTAssertEqual(model.state.stage, .connected)
        XCTAssertEqual(session.connectedCount, 1)
    }

    /// Entering the connected state twice would start a second timer over the first.
    func testASecondConnectedLinkDoesNotRestartTheTimer() async {
        let model = makeModel()
        model.start()
        await settle()

        engine.emit(.connected)
        engine.emit(.connected)

        XCTAssertEqual(session.connectedCount, 1)
    }

    // MARK: - Hanging up

    /// A call that connected ends `completed`; one abandoned while it was still ringing is
    /// `cancelled`, which is the caller's own word for it.
    func testHangingUpAConnectedCallReportsCompleted() async {
        let model = makeModel()
        model.start()
        await settle()
        engine.emit(.connected)

        model.hangUp()
        await settle()

        XCTAssertEqual(contact.outcomes.map(\.outcome), [CallOutcome.completed])
        XCTAssertEqual(session.ends, [.localEnded])
        XCTAssertEqual(engine.leaveCount, 1)
        XCTAssertTrue(model.state.isFinished)
    }

    func testHangingUpWhileConnectingReportsCancelled() async {
        let model = makeModel()
        model.start()
        await settle()

        model.hangUp()
        await settle()

        XCTAssertEqual(contact.outcomes.map(\.outcome), [CallOutcome.cancelled])
    }

    /// A call that had already failed has reported its outcome and does not report a second.
    func testHangingUpAfterAFailureDoesNotReportASecondOutcome() async {
        engine.linkOnJoin = .failed(.media)
        let model = makeModel()
        model.start()
        await settle()

        model.hangUp()
        await settle()

        XCTAssertEqual(contact.outcomes.map(\.outcome), [CallOutcome.voipFailed])
    }

    /// The system's own End — the lock-screen button — is the same hang-up.
    func testTheSystemsOwnEndHangsUp() async {
        let model = makeModel()
        model.start()
        await settle()
        engine.emit(.connected)

        session.onSystemEnd?()
        await settle()

        XCTAssertTrue(model.state.isFinished)
        XCTAssertEqual(engine.leaveCount, 1)
    }

    // MARK: - AL-48 · the fallback

    /// A failed call with a number to fall back to offers *"Call normally instead?"*, and nothing
    /// else does — the masked bridge and D-25's SMS relay were both withdrawn.
    func testTheFallbackIsOfferedOnlyOnAFailureThatHasANumber() async {
        engine.linkOnJoin = .failed(.noMediaClient)
        let model = makeModel()
        model.start()
        await settle()

        XCTAssertTrue(model.state.canDialDirectly)
    }

    /// A ride that has ended answers `409 ride-terminal` and carries no `counterpartyPhone`: there is
    /// nobody left to reach, and offering a dial would be wrong advice rather than a fallback.
    func testATerminalRideOffersNoFallbackBecauseThereIsNobodyToDial() async {
        rides.detailToReturn = rideDetail(counterpartyPhone: nil)
        engine.linkOnJoin = .failed(.signalling)
        let model = makeModel()

        model.start()
        await settle()

        XCTAssertEqual(model.state.stage, .failed)
        XCTAssertFalse(model.state.canDialDirectly)
    }

    /// The `direct_dial` row is written **before** the dial, so the platform can tell a fallback from
    /// a driver who simply preferred to dial.
    func testTheFallbackLogsADirectDialAndThenDials() async {
        engine.linkOnJoin = .failed(.noMediaClient)
        let model = makeModel()
        model.start()
        await settle()

        model.dialDirectly()
        await settle()

        XCTAssertEqual(contact.roleCalls.map(\.type), [CallType.freeVoip, CallType.directDial])
        XCTAssertEqual(contact.dialled, ["+94771234567"])
        XCTAssertTrue(model.state.isFinished)
    }

    /// **The reported call is ended before the fallback is offered**, so `CXCallObserver` is clear
    /// when the driver takes it. Getting this order wrong is a *"Call normally"* button that silently
    /// does nothing, because `SystemRideContact.dial` refuses while the system has a call up.
    func testTheReportedCallIsEndedOnTheWayIntoTheFailureState() async {
        let model = makeModel()
        model.start()
        await settle()
        engine.emit(.connecting)

        engine.emit(.failed(.media))

        XCTAssertEqual(session.ends, [.failed], "ended as a failure, and before any dial is offered")
    }

    /// A dial the system refuses becomes copy rather than a silent no-op.
    func testARefusedDialIsCopyAndTheScreenStaysOpen() async {
        engine.linkOnJoin = .failed(.noMediaClient)
        contact.dialSucceeds = false
        let model = makeModel()
        model.start()
        await settle()

        model.dialDirectly()
        await settle()

        XCTAssertEqual(model.state.errorKey, "ride_call_unavailable")
        XCTAssertFalse(model.state.isFinished)
    }

    // MARK: - The toggles

    func testMuteMovesTheEngineTheSystemAndTheScreenTogether() async {
        let model = makeModel()
        model.start()
        await settle()

        model.toggleMute()

        XCTAssertEqual(engine.mutes, [true])
        XCTAssertEqual(session.mutes, [true])
        XCTAssertTrue(model.state.isMuted)
    }

    /// Control Centre's mute switch moves the engine, not a second toggle: the app follows the
    /// system rather than fighting it.
    func testTheSystemsOwnMuteIsAppliedOnceAndNotEchoedBack() async {
        let model = makeModel()
        model.start()
        await settle()

        session.onSystemMute?(true)

        XCTAssertEqual(engine.mutes, [true])
        XCTAssertTrue(model.state.isMuted)
        XCTAssertTrue(session.mutes.isEmpty, "the system already knows; reporting back would be a loop")
    }

    /// The speaker is an audio-route choice, and CallKit has no action for one.
    func testSpeakerIsNotReportedToCallKit() async {
        let model = makeModel()
        model.start()
        await settle()

        model.toggleSpeaker()

        XCTAssertEqual(engine.speakers, [true])
        XCTAssertTrue(session.mutes.isEmpty)
        XCTAssertTrue(model.state.isSpeakerOn)
    }

    // MARK: - The guard

    /// `.task` can run again after a scene change, and a second run would write a second
    /// `comms.call_log` row for one tap.
    func testStartIsIdempotent() async {
        let model = makeModel()

        model.start()
        await settle()
        model.start()
        await settle()

        XCTAssertEqual(contact.roleCalls.count, 1)
    }

    // MARK: - The copy table

    /// The three causes need different advice: a build with no media client is not a network problem
    /// the driver can wait out, and a ride that has ended has nobody to dial.
    func testEveryFailureHasItsOwnCopy() {
        XCTAssertEqual(VoipCallScreen.failureKey(.signalling), "call_failed_signalling")
        XCTAssertEqual(VoipCallScreen.failureKey(.media), "call_failed_media")
        XCTAssertEqual(VoipCallScreen.failureKey(.noMediaClient), "call_failed_unavailable")
        XCTAssertEqual(VoipCallScreen.failureKey(nil), "call_failed_unavailable")
    }

    /// **The engine this build binds carries no media at all** — see ``AbsentVoipEngine``. The
    /// assertion is here rather than in a fence test because it is the reason every other test in
    /// this file that reaches ``CallStage/connected`` had to drive the link by hand.
    func testTheShippedEngineFailsWithNoMediaClient() {
        var links: [CallLink] = []
        AbsentVoipEngine().join(session: testVoipSession) { links.append($0) }

        XCTAssertEqual(links, [.failed(.noMediaClient)])
    }

    /// `00:42`, not `01:12:40`. A phone call leads with minutes.
    func testTheCallTimerIsMinutesAndSeconds() {
        XCTAssertEqual(MoneyFormat.timer(seconds: 42), "00:42")
        XCTAssertEqual(MoneyFormat.timer(seconds: 605), "10:05")
        XCTAssertEqual(MoneyFormat.timer(seconds: 3_665), "61:05", "a long call grows the minutes, not a field")
        XCTAssertEqual(MoneyFormat.timer(seconds: -1), "00:00")
    }
}
