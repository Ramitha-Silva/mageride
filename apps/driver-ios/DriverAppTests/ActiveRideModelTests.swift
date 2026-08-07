import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-015 · the active ride** — the four rules that are not the wireframe's layout.
///
/// R-14's version echo, AL-47's *"a driver-QR ride is not over at `PaymentPending`"*, AL-48's two call
/// paths, and the sticky terminal that a late poll must not undo.
@MainActor
final class ActiveRideModelTests: XCTestCase {

    private var rides: FakeActiveRideRepository!
    private var contact: FakeRideContact!
    private var location: FakeDriverLocationSource!

    override func setUp() {
        super.setUp()
        rides = FakeActiveRideRepository()
        contact = FakeRideContact()
        location = FakeDriverLocationSource()
    }

    private func makeModel() -> ActiveRideModel {
        ActiveRideModel(rideId: testRideId, rides: rides, contact: contact, location: location)
    }

    // MARK: - The forward CTA

    func testTheCtaIsTheRidesOwnNextStep() async {
        let model = makeModel()

        rides.detailToReturn = rideDetail(state: RideState.accepted)
        await model.refresh()
        XCTAssertEqual(model.state.forwardActionKey, "ride_arrived")
        XCTAssertEqual(model.state.navigationHintKey, "ride_navigate_pickup")

        rides.detailToReturn = rideDetail(state: RideState.driverArrived)
        await model.refresh()
        XCTAssertEqual(model.state.forwardActionKey, "ride_start")

        rides.detailToReturn = rideDetail(state: RideState.inProgress)
        await model.refresh()
        XCTAssertEqual(model.state.forwardActionKey, "ride_complete")
        XCTAssertEqual(model.state.navigationHintKey, "ride_navigate_drop")

        rides.detailToReturn = rideDetail(state: RideState.cashSettled)
        await model.refresh()
        XCTAssertNil(model.state.forwardActionKey)
    }

    /// **R-14** — the version sent is the one the screen was showing, and the response's version is
    /// what the next command carries.
    func testEveryCommandEchoesTheVersionTheScreenWasShowing() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted, version: 3)
        let model = makeModel()
        await model.refresh()

        await model.advance()

        XCTAssertEqual(rides.arrived, [3])
        XCTAssertEqual(model.state.version, 4, "the response IS the new version; no second read")
        XCTAssertEqual(model.state.rideState, RideState.driverArrived)
    }

    /// **Start raises the OTP entry rather than sending anything.**
    func testStartOpensTheOtpSheetAndTheVerifySendsIt() async {
        rides.detailToReturn = rideDetail(state: RideState.driverArrived, version: 5)
        let model = makeModel()
        await model.refresh()

        await model.advance()
        XCTAssertEqual(model.state.sheet, .startOtp)
        XCTAssertTrue(rides.started.isEmpty)

        await model.startRide(otp: "4821")

        XCTAssertNil(model.state.sheet)
        XCTAssertEqual(rides.started.count, 1)
        XCTAssertEqual(rides.started.first?.otp, "4821")
        XCTAssertEqual(rides.started.first?.version, 5)
        XCTAssertEqual(rides.started.first?.kind, RideKind.passenger)
    }

    /// **P-07** — a package has no rider to hold a start OTP, so the sender's pickup OTP goes to the
    /// other endpoint. The repository picks; the screen does not have to know.
    func testAPackageStartsThroughThePickupOtpCommand() async {
        rides.detailToReturn = rideDetail(state: RideState.driverArrived, kind: RideKind.package)
        let model = makeModel()
        await model.refresh()

        await model.startRide(otp: "1234")

        XCTAssertEqual(rides.started.first?.kind, RideKind.package)
    }

    func testAFailedCommandBecomesCopyAndLeavesTheRideWhereItWas() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted, version: 3)
        let model = makeModel()
        await model.refresh()
        rides.nextFailure = apiFailure(code: "version-conflict", status: 409)

        await model.advance()

        XCTAssertEqual(model.state.errorKey, "error_version_conflict")
        XCTAssertEqual(model.state.rideState, RideState.accepted)
        XCTAssertFalse(model.state.isBusy)
    }

    // MARK: - AL-47, the driver-QR settlement

    /// A driver-QR ride is **not over** at `PaymentPending`: the confirm sheet is what settles it.
    func testACompletedDriverQrRideRaisesTheConfirmSheetInsteadOfFinishing() async {
        rides.detailToReturn = rideDetail(state: RideState.inProgress, paymentMethod: RidePaymentMethod.lankaqr)
        let model = makeModel()
        await model.refresh()

        await model.advance()

        XCTAssertEqual(model.state.rideState, RideState.paymentPending)
        XCTAssertTrue(model.state.awaitingQrConfirm)
        XCTAssertEqual(model.state.sheet, .qrConfirm)
        XCTAssertFalse(model.state.isFinished, "the ride is not over until the driver answers")
    }

    /// A **cash** ride at `PaymentPending` has nothing to attest and simply ends.
    func testACashRideNeverRaisesTheConfirmSheet() async {
        rides.detailToReturn = rideDetail(state: RideState.inProgress, paymentMethod: RidePaymentMethod.cash)
        let model = makeModel()
        await model.refresh()

        await model.advance()

        XCTAssertFalse(model.state.awaitingQrConfirm)
        XCTAssertNil(model.state.sheet)
    }

    func testConfirmingTheQrPaymentSettlesTheRideAndAsksOnlyOnce() async {
        rides.detailToReturn = rideDetail(state: RideState.paymentPending, paymentMethod: RidePaymentMethod.lankaqr)
        let model = makeModel()
        await model.refresh()
        XCTAssertTrue(model.state.awaitingQrConfirm)

        await model.confirmQrPayment(received: true)

        XCTAssertEqual(rides.confirmedQr, 1)
        XCTAssertTrue(model.state.isQrAttested)
        XCTAssertTrue(model.state.isFinished)
        XCTAssertFalse(
            model.state.awaitingQrConfirm,
            "the ride sits at PaymentPending until the outbox settles it; asking again is not a thing the driver can answer"
        )
    }

    /// *"Not received"* raises a dispute and moves no money — and the ride is off this driver's hands
    /// either way.
    func testNotReceivedRaisesADisputeRatherThanConfirming() async {
        rides.detailToReturn = rideDetail(state: RideState.completed, paymentMethod: RidePaymentMethod.lankaqr)
        let model = makeModel()
        await model.refresh()

        await model.confirmQrPayment(received: false)

        XCTAssertEqual(rides.disputedQr, 1)
        XCTAssertEqual(rides.confirmedQr, 0)
        XCTAssertTrue(model.state.isFinished)
    }

    // MARK: - AL-48, the two call paths

    /// **Free call leaves this screen.** `POST /v1/calls/start` is SCR-DI-031's, once — making it here
    /// as well would write two `comms.call_log` rows for one tap.
    func testFreeCallNavigatesAndDoesNotStartACallHere() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted)
        let model = makeModel()
        await model.refresh()

        await model.call(type: CallType.freeVoip)

        XCTAssertTrue(model.state.isVoipRequested)
        XCTAssertTrue(contact.calls.isEmpty)
        XCTAssertTrue(contact.dialled.isEmpty)
    }

    func testNormalCallLogsAndDialsTheCounterpartysRealNumber() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted, counterpartyPhone: "+94771234567")
        let model = makeModel()
        await model.refresh()

        await model.call(type: CallType.directDial)

        XCTAssertEqual(contact.calls.count, 1)
        XCTAssertEqual(contact.calls.first?.type, CallType.directDial)
        XCTAssertEqual(contact.dialled, ["+94771234567"])
        XCTAssertFalse(model.state.isVoipRequested)
        XCTAssertNil(model.state.errorKey)
    }

    /// **The CallKit delta.** A `tel:` URL on iOS *places* the call, so dialling over one already up
    /// would hang it up. The refusal is copy, not a silent no-op.
    func testADialRefusedBecauseACallIsUpBecomesCopy() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted)
        contact.dialSucceeds = false
        let model = makeModel()
        await model.refresh()

        await model.call(type: CallType.directDial)

        XCTAssertEqual(model.state.errorKey, "ride_call_unavailable")
        XCTAssertEqual(contact.calls.count, 1, "the log is still written; the dial is what failed")
    }

    // MARK: - SOS, the map and the poll

    /// US-12.8's alarm is a screen with a confirmation, not a button that fires.
    func testSosNavigatesRatherThanRaisingTheAlarmHere() async {
        rides.detailToReturn = rideDetail(state: RideState.inProgress)
        let model = makeModel()
        await model.refresh()

        model.openSos()

        XCTAssertTrue(model.state.isSosRequested)
        model.consume()
        XCTAssertFalse(model.state.isSosRequested)
    }

    /// MAP-10's circle is where the driver is being sent *now* — the pickup until they are on board,
    /// the drop after.
    func testTheGeofenceFollowsTheRideRatherThanTheDriver() async {
        let model = makeModel()

        rides.detailToReturn = rideDetail(state: RideState.accepted)
        await model.refresh()
        XCTAssertEqual(model.state.geofence?.lat, testHere.lat)

        rides.detailToReturn = rideDetail(state: RideState.inProgress)
        await model.refresh()
        XCTAssertEqual(model.state.geofence?.lat, testThere.lat)

        rides.detailToReturn = rideDetail(state: RideState.cashSettled)
        await model.refresh()
        XCTAssertNil(model.state.geofence)
    }

    /// A ride that has finished never un-finishes: a late poll landing after the driver has been sent
    /// back to standby must not undo it.
    ///
    /// Asserted on the state directly rather than through the model, because reproducing it through the
    /// model would mean waiting five seconds for a poll — and the rule is the state's, not the loop's.
    func testTerminalIsSticky() {
        var state = ActiveRideState(ride: rideDetail(state: RideState.inProgress))
        state.advance(to: RideStateSnapshot(state: RideState.cashSettled, version: 8, offerExpiresAt: nil))
        XCTAssertTrue(state.isFinished)

        state.advance(to: RideStateSnapshot(state: RideState.inProgress, version: 9, offerExpiresAt: nil))
        XCTAssertTrue(state.isFinished, "a late poll must not resurrect a ride the driver has left")
    }

    /// A **package** belongs to SCR-DI-016, which polls it itself; two loops folding server states onto
    /// one ride would race each other.
    func testAPackageRideIsNotPolledHere() async {
        rides.detailToReturn = rideDetail(state: RideState.inProgress, kind: RideKind.package)
        let model = makeModel()
        await model.refresh()

        XCTAssertTrue(model.state.isPackage)
        XCTAssertFalse(model.state.isPollable)
    }

    func testAFinishedOrBusyRideIsNotPolled() async {
        rides.detailToReturn = rideDetail(state: RideState.inProgress)
        let model = makeModel()
        await model.refresh()
        XCTAssertTrue(model.state.isPollable)

        rides.detailToReturn = rideDetail(state: RideState.cashSettled)
        await model.refresh()
        XCTAssertFalse(model.state.isPollable)
    }

    func testTheRouteLineIsThePlacesAndTheFare() async {
        rides.detailToReturn = rideDetail(state: RideState.accepted, fareMinor: 48_000)
        let model = makeModel()
        await model.refresh()

        XCTAssertEqual(model.state.routeLine, "Galle Face → Nugegoda · Rs 480")
    }
}
