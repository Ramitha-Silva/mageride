import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

// Cluster 5's rules, asserted with no gateway, no socket and no handset.
//
// Five suites, one per rule that would be expensive to discover on a device: which end of a parcel a
// handset is, which handover code that party may read, the split that fills the Packages tab, the
// second read AL-48 forces on a post-trip Call, and the distance a receipt is allowed to state.

// MARK: - SCR-PI-020 / SCR-PI-021

/// The parcel, from both ends.
final class PackageTrackModelTests: XCTestCase {

    private var history: FakeHistoryRepository!
    private var otps: PackageOtps!
    private var transport: FakeLiveHubTransport!
    private var live: PassengerLiveMap!

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        history = FakeHistoryRepository()
        otps = PackageOtps()
        transport = FakeLiveHubTransport()
        live = PassengerLiveMap(transport: transport, snapshots: FakeNearbySnapshots(), grid: SharedH3Grid())
    }

    @MainActor
    private func model(signedInAs userId: String? = HistoryFixtures.senderId) -> PackageTrackModel {
        PackageTrackModel(
            rideId: HistoryFixtures.rideId,
            history: history,
            live: live,
            otps: otps,
            signedInUserId: userId,
            // Long enough that no assertion in this suite races a re-read.
            pollInterval: 600
        )
    }

    /// **The party is a fact about the ride, not about the URI.** `mageride://package/{rideId}` is
    /// the same link for both ends — the recipient gets it on `package_picked_up`, the sender on
    /// `package_delivered` — so the booker is the sender and everybody else is the recipient.
    @MainActor
    func testTheSenderIsTheBookerAndEverybodyElseIsTheRecipient() {
        let ride = HistoryFixtures.packageRide()

        XCTAssertEqual(
            PackageTrackModel.party(of: ride, signedInUserId: HistoryFixtures.senderId),
            .sender
        )
        XCTAssertEqual(
            PackageTrackModel.party(of: ride, signedInUserId: HistoryFixtures.recipientId),
            .recipient
        )
        // **A recipient never signed in** (P-09, AL-45) — they arrived from a push. The absence of a
        // session is the answer rather than a missing case.
        XCTAssertEqual(PackageTrackModel.party(of: ride, signedInUserId: nil), .recipient)
        // A ride with no booker on it cannot make anybody its sender.
        XCTAssertEqual(
            PackageTrackModel.party(of: HistoryFixtures.packageRide(bookerId: nil),
                                    signedInUserId: HistoryFixtures.senderId),
            .recipient
        )
    }

    /// **Each party reads out their own code and never the other's** (P-07, US-20.4/20.5). The pickup
    /// OTP proves the driver collected from the right sender; the delivery OTP proves they handed it
    /// to the right recipient.
    @MainActor
    func testEachPartySeesOnlyItsOwnHandoverCode() async {
        otps.rememberPickup(rideId: HistoryFixtures.rideId, otp: "4829")
        otps.rememberDelivery(rideId: HistoryFixtures.rideId, otp: "7315")

        let sender = model(signedInAs: HistoryFixtures.senderId)
        await sender.refresh()
        XCTAssertEqual(sender.state.party, .sender)
        XCTAssertEqual(sender.state.otp, "4829")

        let recipient = model(signedInAs: nil)
        await recipient.refresh()
        XCTAssertEqual(recipient.state.party, .recipient)
        XCTAssertEqual(recipient.state.otp, "7315")
    }

    /// **A code nobody told this process is `nil`, and the screen says so** rather than drawing four
    /// empty boxes. Neither OTP has a read — the pickup code is returned once at booking and the
    /// delivery code arrives only on a push — so a cold start or a reinstall has nothing. See
    /// ``PackageOtps`` and the C099 handoff.
    @MainActor
    func testAColdStartHasNoCodeAtAll() async {
        let model = model()
        await model.refresh()
        XCTAssertNil(model.state.otp)
    }

    /// US-20.7's four steps, and `nil` reading as the first rather than as *"unknown"*: before the
    /// driver has confirmed anything, a pickup **is** pending.
    @MainActor
    func testTheFourStepBarMapsEveryStatusAndNilIsTheFirstStep() {
        var state = PackageTrackState()
        XCTAssertEqual(state.step, 0)
        XCTAssertFalse(state.isDelivered)

        state.status = PackageStatus.pickuppending
        XCTAssertEqual(state.step, 0)
        state.status = PackageStatus.pickedup
        XCTAssertEqual(state.step, 1)
        state.status = PackageStatus.intransit
        XCTAssertEqual(state.step, 2)
        state.status = PackageStatus.delivered
        XCTAssertEqual(state.step, PackageTrackState.deliveredStep)
        XCTAssertTrue(state.isDelivered)

        // The bar and the captions cannot disagree about how many steps there are.
        XCTAssertEqual(PackageTrackState.stepKeys.count, PackageTrackState.deliveredStep + 1)
    }

    /// **The socket moves the bar, and only for this ride.** A `PackageStatusChanged` addressed to
    /// another parcel must not advance the one on screen — the passenger may be tracking two.
    @MainActor
    func testAPackageStatusEventMovesTheBarForThisRideOnly() async {
        let model = model()
        model.start()
        // The first read lands before the first event, or the read would overwrite it.
        await eventually("the ride landed") { await MainActor.run { model.state.ride != nil } }
        live.connect()
        await eventually("the socket is up") { await self.transport.connects == 1 }

        await transport.deliver(
            event: IosLiveHub().eventPackageStatus,
            payload: HistoryFixtures.packageStatusPayload(rideId: HistoryFixtures.otherRideId)
        )
        await eventually("nothing moved") { await MainActor.run { model.state.step == 0 } }

        await transport.deliver(
            event: IosLiveHub().eventPackageStatus,
            payload: HistoryFixtures.packageStatusPayload(status: "InTransit")
        )
        await eventually("the bar advanced") { await MainActor.run { model.state.step == 2 } }

        model.stop()
    }

    /// US-6A.12's marker, on the same group.
    @MainActor
    func testADriverPositionMovesTheMarker() async {
        let model = model()
        model.start()
        live.connect()
        await eventually("the socket is up") { await self.transport.connects == 1 }

        await transport.deliver(
            event: IosLiveHub().eventDriverPosition,
            payload: HistoryFixtures.driverPositionPayload()
        )
        await eventually("the marker moved") {
            await MainActor.run { model.state.driverPosition != nil }
        }

        model.stop()
    }

    /// `SubscribeRide` — the caller's **own** ride (`signalr-hub.md` §2.1), and it is the *only*
    /// method this screen ever sends.
    ///
    /// **There is no `UnsubscribeRide` to assert.** §2 has four client → server methods and none of
    /// them leaves a group; ``HubSubscriptions/stopWatchingRide(_:)`` is the client half and its
    /// whole effect is that the ride is not **rejoined** on the next reconnect. The reconnect itself
    /// is not driven here for ``LiveFixtures``' reason — R-09's first delay is up to 1.25 s of real
    /// time and the curve is `:shared`'s to test.
    @MainActor
    func testTheScreenJoinsTheRidesOwnGroupAndNothingElse() async {
        let model = model()
        model.start()
        live.connect()
        await eventually("joined") { await self.transport.methods.contains(IosLiveHub().methodSubscribeRide) }

        let sent = await transport.methods
        XCTAssertEqual(Set(sent), [IosLiveHub().methodSubscribeRide])

        model.stop()
    }

    /// A failed read is copy, never a `ProblemDetails` string (D-26).
    @MainActor
    func testAFailedReadBecomesResolvedCopy() async {
        history.rideFailure = HistoryFakeError.unreachable
        let model = model()
        await model.refresh()
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }
}

// MARK: - SCR-PI-022

/// Past / Scheduled / Packages, and the second read AL-48 forces.
final class TripHistoryModelTests: XCTestCase {

    private var history: FakeHistoryRepository!
    private var sessions: FakePassengerSessions!

    @MainActor
    override func setUp() {
        super.setUp()
        history = FakeHistoryRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = HistoryFixtures.senderId
    }

    @MainActor
    private func model() -> TripHistoryModel {
        TripHistoryModel(history: history, sessions: sessions)
    }

    /// **The Packages tab splits on `CashOnDeliveryCollected`, and that is a contract gap.**
    /// `RideHistoryRow` carries no `kind`, so the only signal is the terminal state a parcel reaches
    /// — P-08's — which means a package paid by any other rail is indistinguishable from a passenger
    /// ride. Adding `kind` to the row is the fix; C081 recorded it and this restates it.
    @MainActor
    func testThePackagesTabIsTheHistoryFilteredByItsOnlySignal() async {
        history.rows = [
            HistoryFixtures.row(rideId: "R1", state: RideState.paid),
            HistoryFixtures.row(rideId: "R2", state: RideState.cashondeliverycollected),
            HistoryFixtures.row(rideId: "R3", state: RideState.cashsettled),
        ]

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.rides.map(\.rideId), ["R1", "R3"])
        XCTAssertEqual(model.state.packages.map(\.rideId), ["R2"])

        model.select(.packages)
        XCTAssertEqual(model.state.visibleRides.map(\.rideId), ["R2"])
        model.select(.past)
        XCTAssertEqual(model.state.visibleRides.map(\.rideId), ["R1", "R3"])
    }

    /// **The fence: a trip cancelled before assignment shows no driver and offers no Call.** There
    /// was never a driver, and the model refuses it as well as the card hiding it — one is what a
    /// passenger sees, the other is what a stale list or a mis-wired callback cannot get around.
    @MainActor
    func testACancelledBeforeAssignmentTripHasNoReachableDriverAndCostsNoRead() async {
        let cancelled = HistoryFixtures.row(state: RideState.cancelledbyriderbeforeaccept, driver: nil)
        let expired = HistoryFixtures.row(state: RideState.expirednodriver, driver: HistoryFixtures.historyDriver())

        XCTAssertFalse(cancelled.hasReachableDriver)
        XCTAssertFalse(expired.hasReachableDriver)
        XCTAssertTrue(HistoryFixtures.row().hasReachableDriver)

        let model = model()
        await model.call(cancelled)
        XCTAssertNil(model.state.callTarget)
        XCTAssertTrue(history.rideReads.isEmpty, "a ride with no driver must not be read at all")
    }

    /// **The number on the card is masked and the Call resolves the real one.**
    /// `RideHistoryRow.driver.mobileMasked` is a `PhoneMasked` and `:shared`'s KDoc forbids parsing
    /// one back; AL-48 put the clear number on `RideDetail.counterpartyPhone`, which only
    /// `GET /v1/rides/{rideId}` carries. So the tap costs exactly one read — and the target carries
    /// the **ride id**, because *"Free call"* opens SCR-PI-028 for a ride.
    @MainActor
    func testACallCostsOneReadAndYieldsTheClearNumberForThatRide() async {
        let row = HistoryFixtures.row(rideId: "R7")
        history.rideDetail = HistoryFixtures.packageRide(counterpartyPhone: HistoryFixtures.driverPhone)

        let model = model()
        await model.call(row)

        XCTAssertEqual(history.rideReads, ["R7"])
        XCTAssertEqual(model.state.callTarget?.phone, HistoryFixtures.driverPhone)
        XCTAssertEqual(model.state.callTarget?.rideId, "R7")
        XCTAssertNotEqual(model.state.callTarget?.phone, HistoryFixtures.maskedPhone)

        model.onCallConsumed()
        XCTAssertNil(model.state.callTarget)
    }

    /// AL-48's rule from the server's side: the number is withheld on a ride cancelled before
    /// assignment. Nothing to dial, and nothing to apologise for — no chooser and no error.
    @MainActor
    func testAWithheldNumberOpensNoChooserAndReportsNoError() async {
        history.rideDetail = HistoryFixtures.packageRide(counterpartyPhone: nil)

        let model = model()
        await model.call(HistoryFixtures.row())

        XCTAssertNil(model.state.callTarget)
        XCTAssertNil(model.state.errorKey)
    }

    /// **No read lists a passenger's own scheduled rides**, so the tab renders its empty state —
    /// `dispatch.yaml` has the *driver's* list and a cancel-by-id and nothing else. See
    /// ``ApiHistoryRepository/scheduled(userId:)``; the day that route exists, this is the assertion
    /// that should start failing.
    @MainActor
    func testTheScheduledTabIsEmptyBecauseNoReadAnswersIt() async {
        history.rows = [HistoryFixtures.row()]
        let model = model()
        await model.refresh()
        model.select(.scheduled)
        await model.loadScheduled()

        XCTAssertTrue(model.state.scheduled.isEmpty)
        XCTAssertTrue(model.state.isEmpty, "an empty Scheduled tab is empty even when Past is not")
        XCTAssertFalse(model.state.rides.isEmpty)
    }

    /// A list that has not answered yet is **loading**, not empty — the illustration is for a
    /// passenger who has genuinely never ridden.
    @MainActor
    func testLoadingIsNotEmptiness() async {
        var state = TripHistoryState()
        XCTAssertTrue(state.isLoading)
        XCTAssertFalse(state.isEmpty)

        state.isLoading = false
        XCTAssertTrue(state.isEmpty)

        history.ridesFailure = HistoryFakeError.unreachable
        let model = model()
        await model.refresh()
        XCTAssertFalse(model.state.isLoading)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }
}

// MARK: - SCR-PI-023

/// One trip, its track and the distance it is allowed to claim.
final class TripDetailsModelTests: XCTestCase {

    private var history: FakeHistoryRepository!
    private var sessions: FakePassengerSessions!

    @MainActor
    override func setUp() {
        super.setUp()
        history = FakeHistoryRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = HistoryFixtures.senderId
    }

    @MainActor
    private func model() -> TripDetailsModel {
        TripDetailsModel(tripId: HistoryFixtures.tripId, history: history, sessions: sessions)
    }

    /// **The polyline is decoded and the distance is the contract's.** `TripDetail.distanceKm` is the
    /// Kalman-filtered figure the fare was computed from (E-04); summing the decoded points here
    /// would produce a second number that disagreed with the receipt the first time a rounding rule
    /// changed server-side.
    @MainActor
    func testTheTrackIsDecodedAndTheDistanceIsTheOneTheFareUsed() async {
        let model = model()
        await model.load()

        XCTAssertEqual(history.tripReads.first?.userId, HistoryFixtures.senderId)
        XCTAssertEqual(history.tripReads.first?.tripId, HistoryFixtures.tripId)
        XCTAssertFalse(model.state.route.isEmpty, "MAP-08's line needs points")
        XCTAssertEqual(model.state.distance, "8.2 km")
        XCTAssertEqual(model.state.total, "Rs 850")
        XCTAssertEqual(model.state.vehicle, "ABC-1234")
        XCTAssertFalse(model.state.isApproximate)
    }

    /// **`operational` geometry makes the distance a lower bound**, and the screen says so. A floor
    /// presented as a measurement is how a passenger ends up arguing with a receipt that never
    /// claimed to be exact.
    @MainActor
    func testAnOperationalTrackMarksTheDistanceApproximate() async {
        history.tripDetail = HistoryFixtures.trip(geometrySource: GeometrySource.operational)

        let model = model()
        await model.load()
        XCTAssertTrue(model.state.isApproximate)

        // `aggregate_1m` omits the distance rather than understating it (`query.yaml`'s own remark),
        // so a trip with no distance has nothing to qualify and the row draws the dash instead.
        history.tripDetail = HistoryFixtures.trip(distanceKm: nil, geometrySource: GeometrySource.telemetry)
        let noDistance = model()
        await noDistance.load()
        XCTAssertFalse(noDistance.state.isApproximate)
        XCTAssertNil(noDistance.state.distance)
    }

    /// The read is `{userId}/{tripId}` and the id is the session's. Signed out there is nothing to
    /// ask for, and the screen stops loading rather than guessing one.
    @MainActor
    func testSignedOutReadsNothing() async {
        sessions.userId = nil

        let model = model()
        await model.load()

        XCTAssertEqual(history.tripReads.count, 0)
        XCTAssertFalse(model.state.isLoading)
        XCTAssertNil(model.state.trip)
    }
}

// MARK: - The labels

/// The two things this cluster renders that are neither a screen nor a read.
final class HistoryLabelTests: XCTestCase {

    /// **A terminal state is translated copy, never the enum's name.** `apps/passenger-android`'s
    /// card prints `row.state.name` — `CashOnDeliveryCollected` on a parcel — which is an
    /// untranslated wire value on a screen D2' §A requires in three languages (Δ C099).
    func testEveryTerminalStateHasItsOwnPill() {
        let terminal: [RideState] = [
            RideState.paid,
            RideState.cashsettled,
            RideState.cashondeliverycollected,
            RideState.cancelledbyriderbeforeaccept,
            RideState.cancelledbyriderafteraccept,
            RideState.cancelledbydriver,
            RideState.expirednodriver,
            RideState.noshowrider,
            RideState.noshowdriver,
        ]

        for state in terminal {
            let pill = RideStateLabel.pill(for: state)
            // The `default:` arm is *"Not paid yet"* and belongs to the two states that are not
            // terminal at all; a terminal one reaching it is a missing case.
            XCTAssertNotEqual(pill.key, "summary_unpaid", "\(state) fell through to the default arm")
            XCTAssertNotEqual(pill.key.localised, pill.key, "no string for \(pill.key)")
        }

        // And the states that are *not* terminal do land there, which is the honest reading of a
        // ride-svc history row that carries one.
        XCTAssertEqual(RideStateLabel.pill(for: RideState.paymentpending).key, "summary_unpaid")

        XCTAssertEqual(RideStateLabel.pill(for: RideState.paid).key, "history_state_paid")
        XCTAssertEqual(RideStateLabel.pill(for: RideState.cancelledbydriver).key, "history_state_cancelled")
    }

    /// **Every time on a history card is read in Colombo, never in the handset's zone** (D-38). The
    /// instant below is 19:00Z on the 17th and 00:30 on the **18th** in Colombo, so a formatter left
    /// on a UTC runner prints the wrong day rather than only the wrong hour.
    func testDatesAreRenderedInColomboRatherThanInTheHandsetsZone() {
        XCTAssertEqual(TripLabels.zone.identifier, "Asia/Colombo")
        XCTAssertEqual(TripLabels.calendar.identifier, .gregorian)

        let morning = HistoryFixtures.timestamp()
        XCTAssertTrue(TripLabels.dateTime(morning).hasSuffix("08:32"), TripLabels.dateTime(morning))

        let crossing = HistoryFixtures.timestamp(HistoryFixtures.afterColomboMidnightMillis)
        let components = TripLabels.calendar.dateComponents([.day, .hour], from: TripLabels.instant(crossing))
        XCTAssertEqual(components.day, 18)
        XCTAssertEqual(components.hour, 0)
    }

    /// The wireframe's `Nugegoda → Galle Face`, and the dash where the server sent no address —
    /// which is every scheduled ride, because `POST /v1/rides/schedule` takes bare coordinates.
    func testARouteWithNoAddressPrintsTheDashRatherThanCoordinates() {
        XCTAssertEqual(TripLabels.route(pickup: "Nugegoda", dropoff: "Galle Face"), "Nugegoda → Galle Face")
        XCTAssertEqual(TripLabels.route(pickup: nil, dropoff: nil), "— → —")
    }
}
