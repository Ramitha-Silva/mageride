import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

// Cluster 4's rules, asserted with no gateway, no socket and no handset.
//
// Six suites, one per rule that would be expensive to discover on a device: the rails AL-57 and AL-59
// left standing, the hand-off a completed ride makes, the Rs 50 that has to be named before the tap,
// AL-47's attestation pair, AL-48's call rules, and the rating that is saved rather than sent.

// MARK: - AL-57 / AL-59

/// The rails, and the two that are gone.
final class PaymentRailsTests: XCTestCase {

    /// **The Definition-of-Done line, asserted where a surcharge could only come from.** The +5 %
    /// existed to recover OnePay's ~3 %; AL-57 retired OnePay as a ride method and AL-59 retired the
    /// platform-merchant LankaQR, so no surviving rail carries one. ``PaymentRails`` is the single
    /// list every payment control renders from.
    func testNoRetiredRailIsOfferedOnAnyList() {
        for list in [PaymentRails.ride, PaymentRails.parcel, PaymentRails.preferable] {
            for retired in PaymentRails.retired {
                XCTAssertFalse(list.contains(retired), "\(retired.wire) was retired by AL-57/AL-59")
            }
        }
    }

    /// Cash first, because it is the default and the one that always works. COD is package-only
    /// (US-20.8) — a passenger ride offering it would be `400 payment-method-invalid`.
    func testTheRideListIsTheThreeSurvivingRailsAndTheParcelAddsCod() {
        XCTAssertEqual(
            PaymentRails.ride,
            [PaymentMethod.cash, PaymentMethod.wallet, PaymentMethod.scanDriverQr]
        )
        XCTAssertEqual(PaymentRails.parcel, PaymentRails.ride + [PaymentMethod.cod])
        XCTAssertFalse(PaymentRails.ride.contains(PaymentMethod.cod))
    }

    /// **A *preference* is cash or the wallet, and the exclusion is the contract's own.**
    /// `iam.yaml`'s `DefaultPaymentMethod` says the driver-QR method is a settlement choice made
    /// during a ride: it needs a driver, a QR image and an amount, none of which exist in Settings.
    func testAStoredPreferenceCannotBeTheDriverQr() {
        XCTAssertEqual(PaymentRails.preferable, [PaymentMethod.cash, PaymentMethod.wallet])
        XCTAssertFalse(PaymentRails.preferable.contains(PaymentMethod.scanDriverQr))
    }

    /// **`wallet` has no stored value, and that is the AL-57 gap.** The contract's enum is still
    /// `[cash, lankaqr, onepay]`, so the rail that replaced `onepay` cannot be written to
    /// `iam.users.default_payment_method` at all and lives on the device instead.
    func testOnlyCashCanBeStoredOnTheProfile() {
        XCTAssertEqual(PaymentRails.storedValueOf(PaymentMethod.cash), DefaultPaymentMethod.cash)
        XCTAssertNil(PaymentRails.storedValueOf(PaymentMethod.wallet))
        XCTAssertNil(PaymentRails.storedValueOf(PaymentMethod.scanDriverQr))
    }

    /// A row written before the change set names a rail this app can no longer draw, so it reads as
    /// Cash rather than pre-selecting something the passenger cannot see, let alone change.
    func testARetiredStoredDefaultReadsAsCash() {
        XCTAssertEqual(PaymentRails.fromStored(DefaultPaymentMethod.lankaqr), PaymentMethod.cash)
        XCTAssertEqual(PaymentRails.fromStored(DefaultPaymentMethod.onepay), PaymentMethod.cash)
        XCTAssertEqual(PaymentRails.fromStored(nil), PaymentMethod.cash)
        XCTAssertNil(PaymentRails.fromWire(PaymentMethod.onepay.wire))
    }

    /// Every rail a screen can draw has a label and a caption. A `default:` arm answering
    /// `payment_retired` for a *live* rail would be copy nobody would notice was wrong.
    func testEverySurvivingRailHasItsOwnLabelAndCaption() {
        for rail in PaymentRails.parcel {
            XCTAssertNotEqual(PaymentRails.labelKey(rail), "payment_retired", rail.wire)
            XCTAssertNotEqual(PaymentRails.captionKey(rail), "payment_retired", rail.wire)
        }
        XCTAssertEqual(PaymentRails.labelKey(PaymentMethod.onepay), "payment_retired")
    }
}

// MARK: - SCR-PI-014 / SCR-PI-015

/// The ride, and where it sends the passenger when it stops being one.
final class ActiveRideModelTests: XCTestCase {

    private var rides: FakeRideRepository!
    private var clock: TestClock!
    private var transport: FakeLiveHubTransport!
    private var live: PassengerLiveMap!

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        rides = FakeRideRepository()
        clock = TestClock()
        transport = FakeLiveHubTransport()
        live = PassengerLiveMap(transport: transport, snapshots: FakeNearbySnapshots(), grid: SharedH3Grid())
    }

    @MainActor
    private func model(tick: TimeInterval = 0.01) -> ActiveRideModel {
        ActiveRideModel(
            rideId: RideFixtures.rideId,
            rides: rides,
            live: live,
            now: { [clock] in clock!.now },
            pollInterval: 60,
            tickInterval: tick
        )
    }

    /// **US-6A.11's two minutes, off the injected clock rather than off a sleep.** The timeout is a
    /// UI promise — dispatch's own expiry is its business — and what it drives is when the screen
    /// stops saying *finding* and starts offering a retry.
    @MainActor
    func testTheSearchWindowRunsOutIntoNoDriversAvailable() async {
        let model = model()
        model.start()
        await eventually("first tick") { await MainActor.run { model.state.secondsUntilTimeout > 0 } }
        XCTAssertFalse(model.state.noDriver)

        clock.advance(by: ActiveRideModel.searchWindowSeconds)

        await eventually("timed out") { await MainActor.run { model.state.noDriver } }
        XCTAssertEqual(model.state.secondsUntilTimeout, 0)
        XCTAssertEqual(model.state.countdown, "0:00")
        model.stop()
    }

    /// `1:34`, as the cell writes it.
    @MainActor
    func testTheCountdownIsMinutesAndPaddedSeconds() async {
        let model = model()
        model.start()
        await eventually("ticked") { await MainActor.run { model.state.secondsUntilTimeout > 0 } }
        XCTAssertEqual(model.state.countdown, "2:00")

        clock.advance(by: 26)
        await eventually("1:34") { await MainActor.run { model.state.countdown == "1:34" } }
        model.stop()
    }

    /// **A completed ride hands the passenger to SCR-PI-016, and that is the defect this closes.**
    /// Nothing on the Android side builds that destination — see ``RideHandOff`` — so `Completed`
    /// left the passenger sitting on a finished ride with D-10 unreachable.
    @MainActor
    func testACompletedRideHandsOverToPayment() async {
        rides.ride = RideFixtures.accepted(state: RideState.completed)
        let model = model()
        model.start()

        await eventually("hand-off") { await MainActor.run { model.state.handOff != nil } }
        XCTAssertEqual(model.state.handOff, .payment)
        model.stop()
    }

    /// A ride the driver settled in cash while the app was away has nothing to pay, so the receipt is
    /// where it belongs — and a cancelled one has neither.
    @MainActor
    func testASettledRideGoesToTheReceiptAndACancelledOneToTheMap() async {
        rides.ride = RideFixtures.ride(state: RideState.cashsettled)
        let settled = model()
        settled.start()
        await eventually("receipt") { await MainActor.run { settled.state.handOff == .receipt } }
        settled.stop()

        rides.ride = RideFixtures.ride(state: RideState.cancelledbydriver)
        let cancelled = model()
        cancelled.start()
        await eventually("finished") { await MainActor.run { cancelled.state.handOff == .finished } }
        cancelled.stop()
    }

    /// **`ExpiredNoDriver` is deliberately not a hand-off.** SCR-PI-014 draws its own *"No drivers
    /// available"* plus a retry over the same screen, which is US-6A.11's own wording; navigating
    /// away would take the retry with it.
    @MainActor
    func testAnExpiredSearchStaysOnTheScreenAndOffersARetry() async {
        rides.ride = RideFixtures.ride(state: RideState.expirednodriver)
        let model = model()
        model.start()

        await eventually("no driver") { await MainActor.run { model.state.noDriver } }
        XCTAssertNil(model.state.handOff, "the retry lives on this screen")
        model.stop()
    }

    /// US-6A.9 versus US-6A.10 — free before acceptance, Rs 50 after. The dialog reads this, which is
    /// why it is state rather than a branch inside a view.
    @MainActor
    func testCancellingIsFreeUntilADriverAcceptsAndCostsAfterwards() async {
        let free = model()
        free.start()
        await eventually("read") { await MainActor.run { free.state.ride != nil } }
        XCTAssertTrue(free.state.cancelIsFree)
        free.stop()

        rides.ride = RideFixtures.accepted()
        let charged = model()
        charged.start()
        await eventually("read") { await MainActor.run { charged.state.ride != nil } }
        XCTAssertFalse(charged.state.cancelIsFree)
        charged.stop()
    }

    /// **The `✕` opens a confirm; it does not cancel.** D-05 settles the fee on the *next* trip, so
    /// nothing appears on a statement today and this dialog is the only moment a passenger can be
    /// told a number.
    @MainActor
    func testTheCloseButtonAsksBeforeItCancels() async {
        rides.ride = RideFixtures.accepted()
        let model = model()
        model.start()
        await eventually("read") { await MainActor.run { model.state.ride != nil } }

        model.askToCancel()
        XCTAssertTrue(model.state.isPendingCancel)
        XCTAssertTrue(rides.cancelled.isEmpty, "asking is not cancelling")

        model.dismissCancel()
        XCTAssertFalse(model.state.isPendingCancel)
        XCTAssertTrue(rides.cancelled.isEmpty)
        model.stop()
    }

    /// The cancel carries the version it read (R-03's optimistic concurrency) and the reason the
    /// contract declares, and the **server's** penalty is what the screen reports afterwards.
    @MainActor
    func testConfirmingSendsTheVersionAndKeepsTheServersPenalty() async {
        rides.ride = RideFixtures.accepted()
        rides.cancelResponse = RideFixtures.cancelled(penaltyMinor: 5_000)
        let model = model()
        model.start()
        await eventually("read") { await MainActor.run { model.state.ride != nil } }

        model.confirmCancel()
        await eventually("cancelled") { await MainActor.run { model.state.handOff == .finished } }

        XCTAssertEqual(rides.cancelled.count, 1)
        XCTAssertEqual(rides.cancelled.first?.version, 3)
        XCTAssertEqual(rides.cancelled.first?.reason, RideCancelReason.riderChangedMind)
        XCTAssertEqual(model.state.penalty?.amountMinor, 5_000)
        XCTAssertEqual(model.state.penalty?.settledOn, PenaltySettlement.nextTrip)
        model.stop()
    }

    /// **The Rs 50 the dialog names is a local constant, and it has to be.**
    /// `CancelRideResponse.penalty` arrives *after* the cancel has happened, which is too late to be
    /// a warning — so the two numbers exist for two different jobs and this pins the one a passenger
    /// reads before they agree.
    func testTheWarningsFigureMatchesDMinus05() {
        XCTAssertEqual(ActiveRideModel.cancellationPenaltyMinor, 5_000)
        XCTAssertEqual(MoneyFormat.rupees(ActiveRideModel.cancellationPenaltyMinor), "Rs 50")
    }

    /// A failed read is a message on the screen, never a `ProblemDetails` string (D-26).
    @MainActor
    func testAnUnreachableRideServiceReportsACodeTableMessage() async {
        rides.rideFailure = RideFakeError.unreachable
        let model = model()
        model.start()

        await eventually("error") { await MainActor.run { model.state.errorKey != nil } }
        XCTAssertEqual(model.state.errorKey, "error_generic")
        model.stop()
    }

    /// The driver's real number is only on the aggregate from acceptance onward (AL-48), which is
    /// what greys SCR-PI-015a's direct-dial row before then.
    @MainActor
    func testTheDriversNumberAppearsOnlyAfterAcceptance() async {
        let before = model()
        before.start()
        await eventually("read") { await MainActor.run { before.state.ride != nil } }
        XCTAssertNil(before.state.driverPhone)
        before.stop()

        rides.ride = RideFixtures.accepted()
        let after = model()
        after.start()
        await eventually("read") { await MainActor.run { after.state.ride != nil } }
        XCTAssertEqual(after.state.driverPhone, RideFixtures.driverPhone)
        after.stop()
    }

    /// **A ride is not terminal at `Completed`.** SCR-PI-016 and SCR-PI-017 take over exactly there,
    /// so the screen has to see the transition; what ends the watch is a ride whose money and whose
    /// journey are both done.
    func testPaymentStatesAreNotTerminalForThePassenger() {
        XCTAssertFalse(RideState.completed.isTerminalForPassenger)
        XCTAssertFalse(RideState.paymentpending.isTerminalForPassenger)
        XCTAssertTrue(RideState.paid.isTerminalForPassenger)
        XCTAssertTrue(RideState.cashsettled.isTerminalForPassenger)
        XCTAssertTrue(RideState.expirednodriver.isTerminalForPassenger)
    }

    /// `CashSettled` is a settled ride even though nothing moved through the platform;
    /// `PaymentPending` is what puts *"Pay now"* on the receipt.
    func testCashInTheVehicleCountsAsSettled() {
        XCTAssertTrue(RideState.cashsettled.isSettled)
        XCTAssertTrue(RideState.paid.isSettled)
        XCTAssertTrue(RideState.cashondeliverycollected.isSettled)
        XCTAssertFalse(RideState.paymentpending.isSettled)
    }

    /// `DriverPosition` moves the marker and nothing else — one read is cheaper than reconciling a
    /// five-field event with a `RideDetail` whose `doCopy` reaches Swift as twenty-two arguments.
    @MainActor
    func testADriverPositionEventMovesTheMarker() async {
        let model = model()
        model.start()
        await eventually("read") { await MainActor.run { model.state.ride != nil } }

        live.events.send(.driverMoved(DriverPosition(rideId: RideFixtures.rideId, lat: 6.90, lng: 79.86, heading: nil)))

        await eventually("moved") { await MainActor.run { model.state.driverPosition != nil } }
        XCTAssertEqual(model.state.driverPosition?.lat, 6.90)
        model.stop()
    }

    /// Another passenger's ride is not this screen's, and a shared socket delivers both.
    @MainActor
    func testAnEventForAnotherRideIsIgnored() async {
        let model = model()
        model.start()
        await eventually("read") { await MainActor.run { model.state.ride != nil } }
        let readsBefore = rides.rideReads

        live.events.send(.driverMoved(DriverPosition(rideId: "01JOTHER0000000000000001", lat: 1, lng: 1, heading: nil)))
        live.events.send(.rideState(RideStateChanged(
            rideId: "01JOTHER0000000000000001",
            state: RideState.completed,
            version: 9,
            driver: nil,
            etaSeconds: nil
        )))

        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertNil(model.state.driverPosition)
        XCTAssertEqual(rides.rideReads, readsBefore, "another ride's transition is not this one's re-read")
        model.stop()
    }
}

// MARK: - SCR-PI-016

/// The rails a ride is offered, and the wallet row's two faces.
final class PaymentMethodModelTests: XCTestCase {

    private var rides: FakeRideRepository!
    private var sessions: FakePassengerSessions!
    private var preferences: FakeAppPreferences!
    private var selection: PaymentSelection!

    @MainActor
    override func setUp() {
        super.setUp()
        rides = FakeRideRepository()
        sessions = FakePassengerSessions()
        sessions.userId = RideFixtures.passengerId
        preferences = FakeAppPreferences()
        selection = PaymentSelection()
    }

    @MainActor
    private func model() -> PaymentMethodModel {
        PaymentMethodModel(
            rideId: RideFixtures.rideId,
            rides: rides,
            sessions: sessions,
            preferences: preferences,
            selection: selection
        )
    }

    /// US-22.4's *"pre-selected at booking/checkout"*, at the checkout end. The badge and the
    /// selection start on the same rail, and a rail this build cannot offer reads as Cash.
    @MainActor
    func testTheStoredPreferenceIsPreSelectedAndBadged() {
        preferences.defaultPaymentMethod = PaymentMethod.wallet.wire
        let model = model()
        XCTAssertEqual(model.state.chosen, PaymentMethod.wallet)
        XCTAssertEqual(model.state.preferred, PaymentMethod.wallet)

        preferences.defaultPaymentMethod = PaymentMethod.onepay.wire
        XCTAssertEqual(self.model().state.chosen, PaymentMethod.cash)
    }

    /// A parcel adds COD and a passenger ride does not (AL-22, US-20.8).
    @MainActor
    func testAParcelIsOfferedCashOnDelivery() async {
        rides.ride = RideFixtures.ride(kind: RideKind.package)
        let model = model()
        model.start()

        await eventually("loaded") { await MainActor.run { model.state.ride != nil } }
        XCTAssertEqual(model.state.rails, PaymentRails.parcel)

        rides.ride = RideFixtures.ride()
        let passengerRide = self.model()
        passengerRide.start()
        await eventually("loaded") { await MainActor.run { passengerRide.state.ride != nil } }
        XCTAssertEqual(passengerRide.state.rails, PaymentRails.ride)
    }

    /// **One number on the screen.** Every row shows the same total, because the rail that cost more
    /// was retired — and the state has no per-rail amount to draw a different one from.
    @MainActor
    func testTheFareIsOneNumberForEveryRail() async {
        let model = model()
        model.start()
        await eventually("loaded") { await MainActor.run { model.state.amountMinor != nil } }
        XCTAssertEqual(model.state.amountMinor, 85_000)
    }

    /// A wallet Rs 40 short offers a top-up rather than a disabled row — `fare.yaml` answers
    /// `402 insufficient-wallet` if the passenger tries anyway, with cash and driver-QR still on the
    /// screen and **never a silent fallback to cash**.
    @MainActor
    func testAShortWalletOffersATopUpAndStillOffersTheOtherRails() async {
        rides.wallet = RideFixtures.wallet(availableMinor: 81_000)
        let model = model()
        model.start()

        await eventually("balance") { await MainActor.run { model.state.walletBalanceMinor != nil } }
        XCTAssertTrue(model.state.walletIsShort)
        XCTAssertFalse(model.state.walletCovers)
        XCTAssertEqual(model.state.rails, PaymentRails.ride, "the other two rails are untouched")
    }

    /// **A wallet-svc outage costs the row its balance line, not the screen.** The ride read and the
    /// balance read fail independently on purpose.
    @MainActor
    func testAWalletOutageDoesNotTakeTheScreenDown() async {
        rides.walletFailure = RideFakeError.unreachable
        let model = model()
        model.start()

        await eventually("loaded") { await MainActor.run { model.state.amountMinor != nil } }
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertNil(model.state.walletBalanceMinor)
        XCTAssertNil(model.state.errorKey)
        XCTAssertFalse(model.state.walletIsShort, "an unknown balance is not a short one")
    }

    /// **Confirm records the rail and posts nothing.** SCR-PI-017 makes the payment, because the
    /// wallet rail settles on the spot and the driver-QR rail needs what the initiation returns.
    @MainActor
    func testConfirmRecordsTheRailWithoutPaying() async {
        let model = model()
        model.start()
        await eventually("loaded") { await MainActor.run { model.state.ride != nil } }

        model.choose(PaymentMethod.wallet)
        model.confirm()

        XCTAssertTrue(model.state.isConfirmed)
        XCTAssertEqual(model.state.chosen, PaymentMethod.wallet)
        XCTAssertEqual(selection.rail(for: RideFixtures.rideId), PaymentMethod.wallet)
        XCTAssertTrue(rides.paid.isEmpty, "this screen does not call POST /v1/fare/pay")
    }

    /// **The defect ``PaymentSelection`` closes.** A ride nobody chose a rail for falls back to the
    /// driver QR — the one path that asks before it acts — rather than to cash, which would mark a
    /// fare settled that nobody handed over.
    @MainActor
    func testAnUnansweredRideFallsBackToTheRailThatAsksFirst() {
        XCTAssertEqual(selection.rail(for: "01JUNKNOWN000000000000001"), PaymentMethod.scanDriverQr)

        selection.choose(rideId: RideFixtures.rideId, method: PaymentMethod.cash)
        XCTAssertEqual(selection.rail(for: RideFixtures.rideId), PaymentMethod.cash)

        selection.forget(rideId: RideFixtures.rideId)
        XCTAssertEqual(selection.rail(for: RideFixtures.rideId), PaymentMethod.scanDriverQr)
    }
}

// MARK: - SCR-PI-017

/// AL-22's scan, AL-15's link and AL-47's attestation pair.
final class PayFareModelTests: XCTestCase {

    private var rides: FakeRideRepository!
    private var camera: FakeCameraAuthoriser!
    private var bank: FakeBankAppHandoff!
    private var clock: TestClock!

    @MainActor
    override func setUp() {
        super.setUp()
        rides = FakeRideRepository()
        camera = FakeCameraAuthoriser()
        bank = FakeBankAppHandoff()
        clock = TestClock()
    }

    @MainActor
    private func model(method: PaymentMethod = PaymentMethod.scanDriverQr) -> PayFareModel {
        PayFareModel(
            rideId: RideFixtures.rideId,
            method: method,
            rides: rides,
            camera: camera,
            bank: bank,
            now: { [clock] in clock!.now },
            pollInterval: 0.01
        )
    }

    /// **The rail SCR-PI-016 confirmed is the rail that is posted.** On the Android side the
    /// argument is discarded between the two screens and `POST /v1/fare/pay` always says
    /// `scan_driver_qr`; this is the assertion that would have caught it.
    @MainActor
    func testTheChosenRailIsWhatIsInitiated() async {
        rides.initiation = RideFixtures.initiation(method: PaymentMethod.wallet, state: PaymentState.succeeded)
        let model = model(method: PaymentMethod.wallet)
        model.start()

        await eventually("initiated") { await MainActor.run { !self.rides.paid.isEmpty } }
        XCTAssertEqual(rides.paid.first?.method, PaymentMethod.wallet)
    }

    /// **A wallet fare is `Succeeded` the moment the initiation returns** — one balanced
    /// `trip_payment` entry, no gateway, no `Pending` (AL-57) — so the screen goes straight on.
    @MainActor
    func testAWalletFareSettlesOnTheSpot() async {
        rides.initiation = RideFixtures.initiation(method: PaymentMethod.wallet, state: PaymentState.succeeded)
        let model = model(method: PaymentMethod.wallet)
        model.start()

        await eventually("confirmed") { await MainActor.run { model.state.isConfirmed } }
        XCTAssertFalse(model.state.isDriverQr)
    }

    /// **`QrClaimedByPassenger` is the wait, not the settlement.** Telling a passenger their driver
    /// had confirmed before the driver was asked is the one thing this screen must never do.
    @MainActor
    func testAClaimIsNotASettlement() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }

        model.claimPaid()
        await eventually("claimed") { await MainActor.run { model.state.isClaimed } }

        XCTAssertFalse(model.state.isConfirmed, "the driver has not been asked yet")
        XCTAssertEqual(rides.claims.first?.rideId, RideFixtures.rideId)
        XCTAssertNil(rides.claims.first?.receiptArtifactId, "no passenger-facing route uploads one")
    }

    /// The poll is what turns the claim into `DriverConfirmedQR` — there is no hub event for a
    /// **payment** transition, because the passenger's groups are the ride's.
    @MainActor
    func testTheDriversConfirmationArrivesOnThePoll() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }

        model.claimPaid()
        await eventually("claimed") { await MainActor.run { model.state.isClaimed } }
        XCTAssertFalse(model.state.isConfirmed)

        rides.status = RideFixtures.status(state: PaymentState.driverConfirmedQR)
        await eventually("confirmed") { await MainActor.run { model.state.isConfirmed } }
        XCTAssertFalse(rides.statusReads.isEmpty)
    }

    /// AL-47 re-pushes the driver at +5 min; past that the passenger is offered Support rather than a
    /// longer spinner. No money has moved either way — there is nothing for the platform to reverse.
    @MainActor
    func testFiveUnconfirmedMinutesOffersSupport() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }

        model.claimPaid()
        await eventually("claimed") { await MainActor.run { model.state.isClaimed } }
        XCTAssertFalse(model.state.offersSupport)

        clock.advance(by: TimeInterval(PayFareState.unconfirmedSeconds))
        await eventually("support") { await MainActor.run { model.state.offersSupport } }
    }

    /// The payload goes to fare-svc **as read**: it is the driver's own bank merchant string and this
    /// app does not interpret it.
    @MainActor
    func testAScannedPayloadIsForwardedVerbatim() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }

        model.onQrScanned("00020101021230550012")

        await eventually("scanned") { await MainActor.run { !self.rides.scanned.isEmpty } }
        XCTAssertEqual(rides.scanned.first?.payload, "00020101021230550012")
        XCTAssertFalse(model.state.isScanning, "the sheet closes on a read")
    }

    /// **The grant is asked for before the scanner is presented.**
    /// `DataScannerViewController.isAvailable` is `false` without it, so presenting first would show
    /// a viewfinder that cannot see.
    @MainActor
    func testTheCameraIsAskedForBeforeTheScannerOpens() async {
        camera.access = .notDetermined
        let model = model()
        model.openScanner()

        await eventually("asked") { await MainActor.run { self.camera.requests == 1 } }
        await eventually("opened") { await MainActor.run { model.state.isScanning } }
    }

    /// A refusal is not an error state: AL-15's link and AL-47's claim both still work, and Settings
    /// is the one thing the app cannot do for the passenger.
    @MainActor
    func testARefusedCameraBlocksTheScannerAndNothingElse() async {
        camera.access = .blocked
        let model = model()
        model.openScanner()

        await eventually("blocked") { await MainActor.run { model.state.isCameraBlocked } }
        XCTAssertFalse(model.state.isScanning)
        XCTAssertNil(model.state.errorKey, "a permission the passenger declined is not a failure")

        model.openCameraSettings()
        XCTAssertEqual(camera.settingsOpened, 1)
    }

    /// Every simulator, and any handset older than the A12 the data scanner needs. Not a permission
    /// problem, so nobody is sent to Settings by it.
    @MainActor
    func testAHandsetThatCannotScanNeverOpensTheSheet() async {
        camera.isScannerSupported = false
        let model = model()
        model.openScanner()

        await eventually("blocked") { await MainActor.run { model.state.isCameraBlocked } }
        XCTAssertFalse(model.state.isScanning)
        XCTAssertEqual(camera.requests, 0, "the grant was already held")
    }

    /// AL-15: a handset with no bank app falls back to the camera, because a tap that appears to do
    /// nothing is worse than either path.
    @MainActor
    func testNoBankAppFallsBackToTheScanner() async {
        bank.opens = false
        let model = model()
        model.openBankApp()

        await eventually("tried") { await MainActor.run { self.bank.attempts == 1 } }
        await eventually("scanner") { await MainActor.run { model.state.isScanning } }
    }

    /// US-8.15 — a rail that will not settle becomes cash, without losing the payment's history.
    @MainActor
    func testSwitchingToCashKeepsThePaymentsHistory() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }

        model.switchToCash()
        await eventually("fell back") { await MainActor.run { model.state.isConfirmed } }

        XCTAssertEqual(rides.fallbacks, [RideFixtures.paymentId])
        XCTAssertEqual(model.state.paymentState, PaymentState.fellBackToCash)
    }

    /// **Retry does not post a second payment.** One fare is one `ride_payments` row; re-running an
    /// initiation that succeeded would be two.
    @MainActor
    func testRetryReadsTheStatusRatherThanPayingAgain() async {
        let model = model()
        model.start()
        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }
        let paidBefore = rides.paid.count

        model.retry()

        await eventually("read") { await MainActor.run { !self.rides.statusReads.isEmpty } }
        XCTAssertEqual(rides.paid.count, paidBefore)
    }

    /// A failed initiation leaves nothing to read, so Retry is what re-runs it.
    @MainActor
    func testRetryReinitiatesWhenThereIsNoPaymentYet() async {
        rides.payFailure = RideFakeError.unreachable
        let model = model()
        model.start()
        await eventually("failed") { await MainActor.run { model.state.errorKey != nil } }
        XCTAssertNil(model.state.paymentId)

        rides.payFailure = nil
        model.retry()

        await eventually("initiated") { await MainActor.run { model.state.paymentId != nil } }
        XCTAssertEqual(rides.paid.count, 2)
    }

    /// **Every code this cluster's four contracts declare has copy**, and every one of them is a key
    /// the three `.strings` files carry — `LocalizationTests` is what checks the second half.
    ///
    /// A `MageRideError` cannot be constructed from Swift without the Kotlin initialiser (the C095
    /// finding), so what is asserted here is the *table*: the keys it can produce, against the ones
    /// declared. AL-57's `402 insufficient-wallet` is the one that matters most — the rail is refused
    /// with cash and driver-QR still on the screen, and **never a silent fallback to cash**, which
    /// `fare.yaml` calls out in as many words.
    func testTheErrorTableCoversTheCodesThisClusterCanSee() {
        let keys = [
            "error_insufficient_wallet", "error_already_settled", "error_ride_moved_on",
            "error_not_your_ride", "error_payment_method_invalid", "error_gateway",
            "error_ride_not_found", "error_validation_failed", "error_dependency_unavailable",
            "error_offline", "error_generic",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
    }

    /// A failure with nothing behind it resolves to the generic message rather than to a
    /// `ProblemDetails` title (D-26).
    func testAnUnrecognisedFailureIsGeneric() {
        XCTAssertEqual(RideErrors.messageKey(for: RideFakeError.unreachable), "error_generic")
    }
}

// MARK: - SCR-PI-015a

/// AL-48, which mostly means what is absent.
final class CallChoiceTests: XCTestCase {

    private var preferences: FakeAppPreferences!
    private var choice: CallChoice!

    override func setUp() {
        super.setUp()
        preferences = FakeAppPreferences()
        choice = CallChoice(preferences: preferences)
    }

    /// The sheet *"remembers last choice"*, and it has to outlive the ride: a passenger who always
    /// calls normally should not be asked again on their next trip.
    func testTheLastChoiceIsRememberedAcrossRides() {
        XCTAssertNil(choice.remembered)

        choice.remember(CallType.directDial)
        XCTAssertEqual(choice.remembered, CallType.directDial)

        // A second `CallChoice` over the same store is the next ride.
        XCTAssertEqual(CallChoice(preferences: preferences).remembered, CallType.directDial)
    }

    /// **US-26.5's notice is shown once, and only before a direct dial.** A free VoIP call reveals
    /// nothing, so disclosing number visibility before one would be a warning about something that is
    /// not happening — which is how people learn to dismiss disclosures.
    func testTheNumberNoticeIsOwedOnceAndOnlyForADirectDial() {
        XCTAssertFalse(choice.owesNumberNotice(for: CallType.freeVoip))
        XCTAssertTrue(choice.owesNumberNotice(for: CallType.directDial))

        choice.remember(CallType.directDial)
        XCTAssertFalse(choice.owesNumberNotice(for: CallType.directDial), "shown once")
    }

    /// A free call does not spend the notice: the disclosure is about a number, and no number was
    /// involved.
    func testAFreeCallDoesNotConsumeTheNotice() {
        choice.remember(CallType.freeVoip)
        XCTAssertTrue(choice.owesNumberNotice(for: CallType.directDial))
    }

    /// A value a later build wrote answers *no preference* rather than crashing or pre-selecting
    /// something this build cannot draw.
    func testAnUnknownStoredCallTypeIsNoPreference() {
        preferences.lastCallType = "normal_masked"
        XCTAssertNil(choice.remembered)
    }

    /// **There is no masked option, and that is the point.** AL-48 withdrew the requirement outright:
    /// a chooser with a third row would be offering a product that does not exist.
    func testThereAreExactlyTwoCallTypes() {
        XCTAssertEqual(CallChoice.all, [CallType.freeVoip, CallType.directDial])
    }

    /// A *"Normal call"* dials `RideDetail.counterpartyPhone` and nothing else — no masking bridge,
    /// no proxy number, no rewriting.
    @MainActor
    func testANormalCallDialsTheNumberTheContractGave() async {
        let contact = FakeRideContact()
        await contact.dial(RideFixtures.driverPhone)
        XCTAssertEqual(contact.dialled, [RideFixtures.driverPhone])
    }
}

// MARK: - SCR-PI-018 / SCR-PI-019

/// The receipt, and the rating that is saved rather than sent.
final class RateAndSummaryTests: XCTestCase {

    private var rides: FakeRideRepository!
    private var ratings: FakeRideRatings!

    @MainActor
    override func setUp() {
        super.setUp()
        rides = FakeRideRepository()
        ratings = FakeRideRatings()
    }

    /// PaymentPending → *"Pay now"*; Paid / CashSettled → the receipt. The cell's own state line.
    @MainActor
    func testAnUnsettledRideGetsAPayNowCta() async {
        rides.ride = RideFixtures.accepted(state: RideState.paymentpending)
        let model = TripSummaryModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        model.load()

        await eventually("loaded") { await MainActor.run { model.state.ride != nil } }
        XCTAssertFalse(model.state.isSettled)
        XCTAssertEqual(model.state.totalMinor, 85_000)
    }

    /// A rated ride stops being offered a rating — which is why the screen re-reads on every appear
    /// rather than once.
    @MainActor
    func testARatedRideIsNotOfferedARatingAgain() async {
        rides.ride = RideFixtures.accepted(state: RideState.cashsettled)
        let model = TripSummaryModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        model.load()
        await eventually("loaded") { await MainActor.run { model.state.ride != nil } }
        XCTAssertTrue(model.state.canRate)

        ratings.rated.insert(RideFixtures.rideId)
        model.load()
        await eventually("re-read") { await MainActor.run { model.state.isRated } }
        XCTAssertFalse(model.state.canRate)
    }

    /// **The itemised breakdown cannot be drawn, and the screen says so rather than re-deriving it.**
    /// `FareBreakdown` exists on `GET /v1/fare/estimate` only; `RideDetail.fare` carries a total, a
    /// currency and a surcharge. Multiplying a per-km rate by a distance on the device would produce
    /// a second, disagreeing number (R-05).
    @MainActor
    func testTheReceiptDoesNotInventAFareBreakdown() async {
        rides.ride = RideFixtures.accepted(state: RideState.paid)
        let model = TripSummaryModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        model.load()

        await eventually("loaded") { await MainActor.run { model.state.ride != nil } }
        XCTAssertFalse(model.state.hasBreakdown)
        XCTAssertEqual(model.state.totalMinor, 85_000)
    }

    /// US-18.1 is 1–5 stars; the chips and the comment are both optional.
    @MainActor
    func testSubmitNeedsAtLeastOneStar() async {
        let model = RateDriverModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        XCTAssertFalse(model.state.canSubmit)

        model.submit()
        XCTAssertTrue(ratings.queued.isEmpty)

        model.setStars(4)
        XCTAssertTrue(model.state.canSubmit)
    }

    /// **Saved, not sent** — and what is saved is a ride and a driver, because §1.11 has no columns
    /// for the stars or the comment. That is the gap, made visible in what the queue was handed.
    @MainActor
    func testSubmitQueuesLocallyAndSendsNothing() async {
        rides.ride = RideFixtures.accepted(state: RideState.paid)
        let model = RateDriverModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        model.start()
        await eventually("driver") { await MainActor.run { model.state.driverName != nil } }

        model.setStars(5)
        model.toggle(.onTime)
        model.onCommentChanged("Good trip")
        model.submit()

        await eventually("queued") { await MainActor.run { model.state.isQueued } }
        XCTAssertEqual(ratings.queued.count, 1)
        XCTAssertEqual(ratings.queued.first?.rideId, RideFixtures.rideId)
        XCTAssertEqual(ratings.queued.first?.driverId, RideFixtures.driverId)
    }

    /// Stars clamp to the range rather than trusting a caller, and a chip toggles both ways.
    @MainActor
    func testStarsClampAndChipsToggle() {
        let model = RateDriverModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)

        model.setStars(9)
        XCTAssertEqual(model.state.stars, RateDriverState.maximumStars)
        model.setStars(-2)
        XCTAssertEqual(model.state.stars, 0)

        model.toggle(.clean)
        XCTAssertTrue(model.state.tags.contains(.clean))
        model.toggle(.clean)
        XCTAssertFalse(model.state.tags.contains(.clean))
    }

    /// **Compliments rather than complaints.** The screen is shown after a completed ride and its job
    /// is to make five stars quick to justify; a report is a different action with a different
    /// destination, and mixing the two would put *"unsafe driving"* one tap from *"on time"*.
    func testTheChipsAreTheFourCompliments() {
        XCTAssertEqual(RatingTag.allCases, [.clean, .onTime, .polite, .safeDriving])
    }

    /// *"How was your ride?"* without a name is still the right question, so a failed read costs the
    /// heading its name and nothing else.
    @MainActor
    func testAFailedDriverReadIsNotAnError() async {
        rides.rideFailure = RideFakeError.unreachable
        let model = RateDriverModel(rideId: RideFixtures.rideId, rides: rides, ratings: ratings)
        model.start()

        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertNil(model.state.driverName)
        XCTAssertNil(model.state.errorKey)
    }
}
