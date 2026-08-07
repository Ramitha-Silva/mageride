import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

// Cluster 6's rules, asserted with no gateway, no socket and no handset.
//
// Six suites, one per rule that would be expensive to discover on a device: the per-vehicle request
// and its two doors, the card list and the marker that goes with a grant, AL-49's `payTo` and the four
// rails AL-59 left behind, the statement's ordering, the signed link the QR rides on, and the code
// table D-26 makes the only source of a failure message.

// MARK: - SCR-PI-024

/// Asking a private vehicle's owner for access (AL-23, US-4.6).
final class ModeBRequestModelTests: XCTestCase {

    private var repository: FakeSubscriptionRepository!
    private var sessions: FakePassengerSessions!
    private var keys: FakeIdempotencyKeys!

    @MainActor
    override func setUp() {
        super.setUp()
        repository = FakeSubscriptionRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = SubscriptionFixtures.passengerId
        keys = FakeIdempotencyKeys()
    }

    @MainActor
    private func model(vehicleId: String?) -> ModeBRequestModel {
        ModeBRequestModel(
            vehicleId: vehicleId,
            subscriptions: repository,
            sessions: sessions,
            keys: keys
        )
    }

    /// **AL-23's two doors, and the route is what carries the difference.** A Mode B marker tap
    /// pre-fills the Vehicle ID; the Menu tab's *"Private transport"* row does not. The path is
    /// asserted alongside the model because `NavigationShellTests` diffs it against Kotlin's, and this
    /// is the one route in the table whose argument is optional.
    @MainActor
    func testAMarkerTapArrivesPreFilledAndTheMenuRowDoesNot() {
        XCTAssertEqual(
            PassengerRoute.modeBRequest(vehicleId: SubscriptionFixtures.vehicleId).path,
            "private-transport?vehicleId=\(SubscriptionFixtures.vehicleId)"
        )
        XCTAssertEqual(PassengerRoute.modeBRequest(vehicleId: nil).path, "private-transport")
        XCTAssertEqual(PassengerMenuDestination.privateTransport.route, .modeBRequest(vehicleId: nil))

        let fromMarker = model(vehicleId: SubscriptionFixtures.vehicleId)
        XCTAssertEqual(fromMarker.state.vehicleId, SubscriptionFixtures.vehicleId)
        XCTAssertTrue(fromMarker.state.isPrefilled)

        let fromMenu = model(vehicleId: nil)
        XCTAssertEqual(fromMenu.state.vehicleId, "")
        XCTAssertFalse(fromMenu.state.isPrefilled)
    }

    /// Nothing is sent until there is an id, and a pasted one is trimmed — the server's answer to
    /// `" MR-VEH-48213"` is a 404 nobody can act on.
    @MainActor
    func testNothingIsSentUntilAVehicleIdIsPresent() async {
        let model = model(vehicleId: nil)
        XCTAssertFalse(model.state.canSend)

        await model.send()
        XCTAssertTrue(repository.accessRequested.isEmpty, "an empty field sends nothing")

        model.onVehicleIdChange("  \(SubscriptionFixtures.vehicleId)  ")
        XCTAssertEqual(model.state.vehicleId, SubscriptionFixtures.vehicleId)
        XCTAssertTrue(model.state.canSend)
    }

    /// **One request per vehicle, and a double tap is one of them** (R-14). The Pending chip is the
    /// only decision this screen can actually observe.
    @MainActor
    func testSendingRaisesOneRequestPerVehicleAndShowsThePendingChip() async {
        let model = model(vehicleId: SubscriptionFixtures.vehicleId)

        await model.send()

        XCTAssertTrue(model.state.isPending)
        XCTAssertEqual(repository.accessRequested, [SubscriptionFixtures.vehicleId])
        XCTAssertEqual(repository.idempotencyKeys.compactMap { $0 }.count, 1)

        // A second tap while the request is pending must not raise a duplicate.
        XCTAssertFalse(model.state.canSend)
        await model.send()
        XCTAssertEqual(repository.accessRequested.count, 1)
    }

    /// **Accepted is inferred from the subscription, because nothing else on this surface says so.**
    /// There is no passenger-facing read of one's own access requests and notification-svc mints no
    /// Mode B push kind; an accepted request creates a subscription in the same transaction, so a
    /// subscription for this vehicle *is* the accept. Recorded in the C100 handoff.
    @MainActor
    func testAVehicleAlreadySubscribedToReadsAsAcceptedRatherThanAsANewRequest() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]

        let model = model(vehicleId: SubscriptionFixtures.vehicleId)
        await model.loadExisting()

        XCTAssertTrue(model.state.isAccepted)
        XCTAssertNotNil(model.state.existing)
        XCTAssertFalse(model.state.canSend, "asking again would be a 409")
    }

    /// A courtesy, not a precondition: a failed lookup leaves the screen able to ask.
    @MainActor
    func testAFailedLookupIsSilentAndDoesNotBlockTheRequest() async {
        repository.failWith = SubscriptionFakeError.unreachable

        let model = model(vehicleId: SubscriptionFixtures.vehicleId)
        await model.loadExisting()

        XCTAssertNil(model.state.existing)
        XCTAssertNil(model.state.errorKey)
        XCTAssertTrue(model.state.canSend)
    }

    /// D-26 — the kebab code is the key and the copy is `Localizable.strings`'. *"Something went
    /// wrong"* would send a passenger back to a field that is correct except for one character, and
    /// the field stays sendable so they can fix it.
    @MainActor
    func testAFailedRequestBecomesResolvedCopyAndLeavesTheFieldSendable() async {
        let model = model(vehicleId: nil)
        model.onVehicleIdChange("MR-VEH-00000")
        repository.failWith = SubscriptionFakeError.unreachable

        await model.send()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertTrue(model.state.canSend)

        model.onVehicleIdChange("MR-VEH-00001")
        XCTAssertNil(model.state.errorKey, "typing clears a failure about the previous attempt")
    }

    /// The three decisions all have copy, and only the accepted one offers the link onward.
    func testEveryDecisionHasAPillAndANote() {
        for status in [AccessRequestStatus.pending, AccessRequestStatus.accepted, AccessRequestStatus.rejected] {
            let labels = ModeBRequestScreen.decisionLabels(for: status)
            XCTAssertNotEqual(labels.titleKey.localised, labels.titleKey, "\(status) has no pill copy")
            XCTAssertNotEqual(labels.noteKey.localised, labels.noteKey, "\(status) has no note copy")
        }
        XCTAssertEqual(ModeBRequestScreen.decisionLabels(for: AccessRequestStatus.accepted).tone, .ok)
        XCTAssertEqual(ModeBRequestScreen.decisionLabels(for: AccessRequestStatus.rejected).tone, .error)
    }
}

// MARK: - SCR-PI-025

/// The cards, the pills, and the Definition-of-Done line *"unsubscribing removes the vehicle from the
/// live map within seconds"*.
///
/// That line is asserted against a **real** ``PassengerLiveMap`` over the fake socket rather than
/// against a mock, because the interesting part is the join between two subsystems: the unsubscribe is
/// subscription-svc's and the marker is fanout-svc's, and the client is what closes the gap before
/// `share.revoked` comes back (D-22, AL-25).
final class SubscriptionsModelTests: XCTestCase {

    private var repository: FakeSubscriptionRepository!
    private var sessions: FakePassengerSessions!
    private var keys: FakeIdempotencyKeys!
    private var transport: FakeLiveHubTransport!
    private var live: PassengerLiveMap!

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        repository = FakeSubscriptionRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = SubscriptionFixtures.passengerId
        keys = FakeIdempotencyKeys()
        transport = FakeLiveHubTransport()
        live = PassengerLiveMap(transport: transport, snapshots: FakeNearbySnapshots(), grid: SharedH3Grid())
    }

    @MainActor
    private func model() -> SubscriptionsModel {
        SubscriptionsModel(subscriptions: repository, sessions: sessions, live: live, keys: keys)
    }

    /// BR-23.8 — Free is office and staff transport: no fee, and no payment UI at all. The absence of
    /// a fare is `ck_subscriptions_fare`'s shape, not a missing field.
    @MainActor
    func testAPaidCardCarriesAFareAndAFreeOneCarriesNone() async {
        repository.held = [SubscriptionFixtures.paidSubscription(), SubscriptionFixtures.freeSubscription()]

        let model = model()
        await model.refresh()

        let paid = model.state.cards.first { $0.id == SubscriptionFixtures.subscriptionId }
        let free = model.state.cards.first { $0.id == SubscriptionFixtures.freeSubscriptionId }

        XCTAssertEqual(paid?.fare?.amountMinor, SubscriptionFixtures.monthlyFareMinor)
        XCTAssertEqual(paid?.isPaid, true, "💳 Pay and 🧾 are drawn")
        XCTAssertNil(free?.fare)
        XCTAssertEqual(free?.isPaid, false, "a Free vehicle has nothing to pay and no statement to read")
        XCTAssertEqual(free.map { SubscriptionLabels.cardPill($0).titleKey }, "subscriptions_free")

        // Only the Paid subscription costs a statement read — a Free month has none to have.
        XCTAssertEqual(repository.paymentReads, [SubscriptionFixtures.subscriptionId])
    }

    /// The Definition-of-Done line *"an online-transfer payment shows Pending verification until the
    /// owner confirms"*, seen from the card rather than from the statement. A passenger who has
    /// already sent the money must not be told they owe it.
    @MainActor
    func testATransferTheOwnerHasNotConfirmedShowsPendingVerificationOnTheCard() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]
        repository.payments = [
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.onlineTransfer,
                status: SubscriptionPaymentStatus.pendingVerification
            ),
        ]

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.cards.first?.monthStatus, SubscriberMonthStatus.pendingVerification)
        XCTAssertEqual(
            SubscriptionLabels.cardPill(model.state.cards[0]).titleKey,
            "subscriptions_status_pending"
        )
    }

    /// `GET …/payments` fixes no ordering, so the newest period is taken by comparison. A client that
    /// trusted position would print April's status over June's.
    @MainActor
    func testThePillIsReadFromTheLatestMonthAndNotFromTheFirstRowReturned() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]
        repository.payments = [
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.cash,
                status: SubscriptionPaymentStatus.initiated,
                paymentId: "01JPAY00000000000000000002",
                monthMillis: SubscriptionFixtures.mayMillis
            ),
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.lankaqrScan,
                status: SubscriptionPaymentStatus.paid,
                paymentId: "01JPAY00000000000000000003",
                monthMillis: SubscriptionFixtures.juneMillis
            ),
        ].reversed()

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.cards.first?.monthStatus, SubscriberMonthStatus.paid)
    }

    /// **The marker goes with the grant, and nothing is sent to the hub to make it.**
    /// `signalr-hub.md` §2 has four client → server methods and none of them leaves a
    /// `vehicle:{vehicleId}` group — membership is the server's, granted at join from the
    /// `share:{userId}` entitlement (D-23). What the client owns is erasing the marker it already drew.
    @MainActor
    func testUnsubscribingDropsTheCardAndTheMarkerWithoutWaitingForTheSocket() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]

        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.deliver(
            event: IosLiveHub().eventVehiclePositions,
            payload: LiveFixtures.vehiclePositions([SubscriptionFixtures.vehicleId], type: "van")
        )
        await eventually("the subscribed van is on the map") {
            await MainActor.run { self.live.vehicles.count } == 1
        }
        await transport.clearSent()

        let model = model()
        await model.refresh()
        let card = model.state.cards[0]

        // The ✕ asks first: AL-25 makes this irreversible in place.
        model.confirmUnsubscribe(card)
        XCTAssertEqual(model.state.confirming, card)
        XCTAssertTrue(repository.unsubscribed.isEmpty, "the dialog alone changes nothing")

        await model.unsubscribe(card)

        XCTAssertEqual(repository.unsubscribed, [SubscriptionFixtures.subscriptionId])
        XCTAssertTrue(model.state.cards.isEmpty)
        XCTAssertNil(model.state.leaving)
        await eventually("the marker went with the grant — no ShareRevoked needed") {
            await MainActor.run { self.live.vehicles.isEmpty }
        }
        let methods = await transport.methods
        XCTAssertTrue(methods.isEmpty, "the client never asks to leave a vehicle group")
    }

    /// The row is removed on the response, not on the tap: a passenger whose unsubscribe failed still
    /// has the subscription, and hiding it would mean they could not try again.
    @MainActor
    func testAFailedUnsubscribeLeavesTheCardWhereItWas() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]
        let model = model()
        await model.refresh()

        repository.failWith = SubscriptionFakeError.unreachable
        await model.unsubscribe(model.state.cards[0])

        XCTAssertEqual(model.state.cards.count, 1)
        XCTAssertNil(model.state.leaving)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    /// A list that has not answered yet is **loading**, not empty.
    @MainActor
    func testLoadingIsNotEmptiness() async {
        var state = SubscriptionsState()
        XCTAssertTrue(state.isLoading)
        XCTAssertFalse(state.isEmpty)
        state.isLoading = false
        XCTAssertTrue(state.isEmpty)

        let model = model()
        await model.refresh()
        XCTAssertFalse(model.state.isLoading)
        XCTAssertTrue(model.state.isEmpty)
    }

    /// **A pill with no statement behind it is never "Paid"**, which is the one error a passenger acts
    /// on — and a statement that is genuinely empty is a month they *owe*, not one nobody knows about.
    ///
    /// The two are different states and the wireframe draws them differently: `nil` is still loading
    /// or unreadable and says *Checking…*, while `monthStatus(nil)` — no payment row for the period —
    /// is `UNPAID`, exactly what the owner's own roster shows.
    @MainActor
    func testAnUnreadStatementSaysCheckingAndAnEmptyOneSaysPaymentDue() async {
        var card = SubscriptionCard(
            subscription: SubscriptionFixtures.paidSubscription(),
            fare: nil,
            monthStatus: nil
        )
        XCTAssertEqual(SubscriptionLabels.cardPill(card).titleKey, "subscriptions_status_unknown")

        card.monthStatus = SubscriberMonthStatus.unpaid
        XCTAssertEqual(SubscriptionLabels.cardPill(card).titleKey, "subscriptions_status_due")

        repository.held = [SubscriptionFixtures.paidSubscription()]
        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.cards.first?.monthStatus, SubscriberMonthStatus.unpaid)
    }
}

// MARK: - SCR-PI-025a

/// Paying one Mode B month, and the two Definition-of-Done lines under it: *"the pay sheet shows the
/// correct payTo for the owning org"* and *"an online-transfer payment shows Pending verification
/// until the owner confirms"*.
///
/// The fence under both is AL-49: `payTo` is minted by `POST …/pay` from a **verified** payout profile
/// and by nothing else, so the sheet cannot show an account number before the payment exists.
final class SubscriptionPayModelTests: XCTestCase {

    private var repository: FakeSubscriptionRepository!
    private var sessions: FakePassengerSessions!
    private var bank: FakeBankAppHandoff!
    private var keys: FakeIdempotencyKeys!

    @MainActor
    override func setUp() {
        super.setUp()
        repository = FakeSubscriptionRepository()
        repository.held = [SubscriptionFixtures.paidSubscription()]
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = SubscriptionFixtures.passengerId
        bank = FakeBankAppHandoff()
        keys = FakeIdempotencyKeys()
    }

    @MainActor
    private func model() async -> SubscriptionPayModel {
        let model = SubscriptionPayModel(
            subscriptionId: SubscriptionFixtures.subscriptionId,
            subscriptions: repository,
            sessions: sessions,
            bank: bank,
            keys: keys
        )
        await model.load()
        return model
    }

    /// **There is no `GET …/subscriptions/{subscriptionId}`**, so the sheet reads the passenger's own
    /// list and picks. C082 recorded the gap; this is what the workaround has to keep true.
    @MainActor
    func testTheSheetOpensOnLankaQrAndShowsTheFareBeforeAnythingIsPaid() async {
        let model = await model()

        XCTAssertEqual(repository.subscriptionReads, [SubscriptionFixtures.passengerId])
        XCTAssertEqual(model.state.method, SubscriptionPayMethod.lankaqrDeeplink, "the pre-selected row")
        XCTAssertEqual(model.state.amount?.amountMinor, SubscriptionFixtures.monthlyFareMinor)
        XCTAssertTrue(model.state.canConfirm)
        // Nothing to show yet: `payTo` does not exist until the payment does.
        XCTAssertNil(model.state.step)
    }

    /// US-23.4. The screenshot is the evidence the owner confirms against; without it the payment
    /// would sit at `initiated` with nothing for them to look at.
    @MainActor
    func testPayingByTransferNeedsTheSlipFirstAndLandsOnPendingVerification() async {
        let model = await model()

        model.choose(SubscriptionPayMethod.onlineTransfer)
        XCTAssertFalse(model.state.canConfirm, "no slip, no confirm")

        await model.attachSlip(fileName: "slip.png", data: Data([1, 2, 3]))
        XCTAssertTrue(model.state.canConfirm)

        await model.confirm()

        XCTAssertEqual(repository.paid.map { $0.method }, [SubscriptionPayMethod.onlineTransfer])
        XCTAssertEqual(repository.slipsUploaded.count, 1, "the slip followed the initiation")
        XCTAssertEqual(model.state.payment?.status, SubscriptionPaymentStatus.pendingVerification)
        XCTAssertFalse(model.state.isAwaitingSlip)
        XCTAssertTrue(model.state.isSettled)
    }

    /// AL-49's *"the pay sheet shows the correct payTo for the owning org"* — the account is never
    /// MageRide's, and it is never rendered from anything but the server's answer.
    @MainActor
    func testTheTransferDetailsAreTheOwnersAndArriveWithThePayment() async {
        let model = await model()
        model.choose(SubscriptionPayMethod.onlineTransfer)
        await model.attachSlip(fileName: "slip.png", data: Data([1]))
        repository.slipAnswer = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.onlineTransfer,
            status: SubscriptionPaymentStatus.initiated
        )

        await model.confirm()

        let step = model.state.step as? ModeBPaymentStepTransferAndUploadSlip
        XCTAssertEqual(step?.payTo.accountHolderName, "ABC Fleet (Pvt) Ltd")
        XCTAssertEqual(step?.payTo.accountNo, "8001234567")
    }

    /// AL-49 again: the image is the fleet owner's bank-app QR, behind a signed URL. This app renders
    /// no QR of its own (AL-22) — it shows theirs, fetched through the link the payment carried.
    @MainActor
    func testTheScanRailResolvesTheOwnersOwnQrImageThroughTheSignedLink() async {
        repository.qrBytes = Data([9, 9, 9])
        let model = await model()
        model.choose(SubscriptionPayMethod.lankaqrScan)

        await model.confirm()

        XCTAssertTrue(model.state.step is ModeBPaymentStepShowOwnerLankaQr)
        XCTAssertEqual(
            repository.qrLinksFetched,
            [SubscriptionFixtures.payTo.lankaqrImageUrl],
            "the signed link the payment carried, not one this app built"
        )
        XCTAssertEqual(model.state.ownerQr?.count, 3)
    }

    /// US-23.6 — `POST …/mark-cash` is the OWNER's operation and answers 403 here. A spinner waiting
    /// for a confirmation this handset can never receive would be a lie.
    @MainActor
    func testCashTellsThePassengerTheOwnerHasToRecordIt() async {
        let model = await model()
        model.choose(SubscriptionPayMethod.cash)

        await model.confirm()

        XCTAssertTrue(model.state.step is ModeBPaymentStepHandToCollector)
        XCTAssertTrue(repository.slipsUploaded.isEmpty, "cash has nothing to photograph")
        XCTAssertTrue(repository.qrLinksFetched.isEmpty, "and no QR to fetch")
    }

    /// **AL-15's ordering: the deep link is the primary path and the code is the fallback.** A handset
    /// where nothing claimed the link re-resolves the step to the payload, which the passenger scans
    /// with their own bank app — and a refused hand-off is never an error state.
    @MainActor
    func testAHandsetWithNoBankAppFallsBackToThePayloadRatherThanFailing() async {
        let model = await model()
        await model.confirm()

        let handoff = model.state.step as? ModeBPaymentStepGatewayHandoff
        let open = handoff?.action as? FarePaymentActionOpenBankApp
        XCTAssertNotNil(open, "the deep link is what a LankaQR rail resolves to first")

        bank.opens = false
        await model.openBankApp(url: open?.url ?? "")

        XCTAssertEqual(bank.openedUrls, [open?.url])
        XCTAssertNil(model.state.errorKey, "a handset with no bank app is not a failed payment")
        let fallback = (model.state.step as? ModeBPaymentStepGatewayHandoff)?.action
        XCTAssertTrue(fallback is FarePaymentActionShowLankaQrFallback)
    }

    /// **A refused pay initiates nothing and leaves the sheet usable** — BR-31.1's
    /// `409 payout-profile-not-verified` is the case that matters, and the fleet's own failure must
    /// not strand the passenger on a dead screen.
    ///
    /// The failure here is a bare Swift error rather than that 409, because a `MageRideError` cannot
    /// be constructed from Swift without the Kotlin initialiser (the C095 finding). What the *copy*
    /// for that code is, and that it exists in three languages, is ``ModeBErrorsTests``' and
    /// `LocalizationTests`'. The failure is armed after the load, because the subscription read comes
    /// first and `failWith` is one-shot.
    @MainActor
    func testARefusedPaymentInitiatesNothingAndLeavesTheSheetUsable() async {
        let model = await model()
        repository.failWith = SubscriptionFakeError.unreachable

        await model.confirm()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertNil(model.state.payment, "nothing was initiated")
        XCTAssertTrue(model.state.canConfirm, "and the sheet can still be used")
    }

    /// The payment row is already typed with the method the server accepted; switching would orphan it
    /// and leave the passenger looking at instructions for a rail nobody charged.
    @MainActor
    func testTheRailCannotChangeOnceThePaymentExists() async {
        let model = await model()
        await model.confirm()

        model.choose(SubscriptionPayMethod.cash)

        XCTAssertEqual(model.state.method, SubscriptionPayMethod.lankaqrDeeplink)
        XCTAssertEqual(repository.paid.count, 1)
    }

    /// R-14 — a double tap is one payment row rather than two months of debt.
    @MainActor
    func testConfirmingTwiceRaisesOnePaymentWithOneKey() async {
        let model = await model()

        await model.confirm()
        await model.confirm()

        XCTAssertEqual(repository.paid.count, 1)
        XCTAssertEqual(repository.idempotencyKeys.compactMap { $0 }.count, 1)
    }
}

/// AL-59, as a table nobody can quietly widen.
///
/// The wireframe for SCR-PI-025a still draws **OnePay · cards / wallets · +5 %**, and rebuilding the
/// sheet from that drawing is the mistake this suite exists to catch: a Mode B subscription is paid to
/// the **fleet owner**, OnePay has one merchant account per merchant, and the money would land in
/// MageRide's. The rail was removed from `subscription.yaml` along with its webhook.
final class SubscriptionRailsTests: XCTestCase {

    func testTheSheetOffersFourRailsAndOnePayIsNotOneOfThem() {
        XCTAssertEqual(
            SubscriptionRails.methods,
            [
                SubscriptionPayMethod.lankaqrDeeplink,
                SubscriptionPayMethod.lankaqrScan,
                SubscriptionPayMethod.onlineTransfer,
                SubscriptionPayMethod.cash,
            ],
            "D2' §16e's four modes, in the wireframe's order"
        )
        XCTAssertFalse(SubscriptionRails.isOffered(SubscriptionPayMethod.onepay), "AL-59 removed it")
        XCTAssertEqual(SubscriptionRails.retired, [SubscriptionPayMethod.onepay])
    }

    /// `SubscriptionPayMethod` types the whole `subscription.payments.method` domain, and SCR-PI-025b
    /// renders history rows written before AL-59 — so a table that fell through to a generic string
    /// would print a blank method on a real statement.
    func testEveryDeclaredMethodHasCopyIncludingTheRetiredOne() {
        let all = SubscriptionRails.methods + SubscriptionRails.retired
        for method in all {
            let label = SubscriptionRails.labelKey(method)
            let caption = SubscriptionRails.captionKey(method)
            XCTAssertNotEqual(label.localised, label, "\(method) has no label")
            XCTAssertNotEqual(caption.localised, caption, "\(method) has no caption")
            XCTAssertFalse(SubscriptionRails.symbolName(method).isEmpty)
        }
    }

    /// **No surviving rail carries a surcharge**, so nothing in this app can render one: the copy is
    /// checked in all three languages for the two words that would mean it did.
    func testNoRailAdvertisesASurchargeInAnyLanguage() {
        let bundle = MageRideColor.bundle
        for locale in ["en", "si", "ta"] {
            guard
                let path = bundle.path(forResource: locale, ofType: "lproj"),
                let localised = Bundle(path: path)
            else {
                XCTFail("cannot read \(locale).lproj")
                continue
            }
            for method in SubscriptionRails.methods {
                let caption = localised.localizedString(
                    forKey: SubscriptionRails.captionKey(method),
                    value: nil,
                    table: "Localizable"
                )
                XCTAssertFalse(caption.contains("5%"), "\(locale)/\(method) still advertises OnePay's +5%")
                XCTAssertFalse(caption.contains("OnePay"), "\(locale)/\(method) still names the retired rail")
            }
        }
    }

    /// BR-23.10, and the reason `pending_verification` is a first-class status: a passenger who has
    /// already transferred the money must not be told they have not paid.
    func testTwoOfTheFourRailsSettleOnTheOwnerAndNotOnAGateway() {
        let rules = ModeBPaymentRules.shared

        XCTAssertTrue(rules.requiresOwnerConfirmation(method: SubscriptionPayMethod.onlineTransfer))
        XCTAssertTrue(rules.requiresOwnerConfirmation(method: SubscriptionPayMethod.cash))
        XCTAssertFalse(rules.requiresOwnerConfirmation(method: SubscriptionPayMethod.lankaqrDeeplink))
        XCTAssertFalse(rules.requiresOwnerConfirmation(method: SubscriptionPayMethod.lankaqrScan))

        // US-23.4 — only the transfer needs a slip. Cash has nothing to photograph.
        XCTAssertTrue(rules.requiresSlip(method: SubscriptionPayMethod.onlineTransfer))
        XCTAssertFalse(rules.requiresSlip(method: SubscriptionPayMethod.cash))
    }
}

// MARK: - SCR-PI-025b

/// The subscriber's statement (US-23.9).
///
/// The wireframe prints June above May above April, and `GET …/payments` promises no order at all — so
/// the ordering is this screen's and is asserted rather than assumed. A statement that listed April at
/// the top reads as a payment nobody made.
final class SubscriptionPaymentsModelTests: XCTestCase {

    private var repository: FakeSubscriptionRepository!
    private var sessions: FakePassengerSessions!

    @MainActor
    override func setUp() {
        super.setUp()
        repository = FakeSubscriptionRepository()
        sessions = FakePassengerSessions()
        sessions.isSignedIn = true
        sessions.userId = SubscriptionFixtures.passengerId
    }

    @MainActor
    private func model() -> SubscriptionPaymentsModel {
        SubscriptionPaymentsModel(
            subscriptionId: SubscriptionFixtures.subscriptionId,
            subscriptions: repository,
            sessions: sessions
        )
    }

    @MainActor
    func testTheStatementIsNewestMonthFirstWhateverOrderTheServerUsed() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]
        repository.payments = [
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.cash,
                status: SubscriptionPaymentStatus.paid,
                paymentId: "01JPAY00000000000000000001",
                monthMillis: SubscriptionFixtures.aprilMillis
            ),
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.lankaqrScan,
                status: SubscriptionPaymentStatus.paid,
                paymentId: "01JPAY00000000000000000003",
                monthMillis: SubscriptionFixtures.juneMillis
            ),
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.onlineTransfer,
                status: SubscriptionPaymentStatus.pendingVerification,
                paymentId: "01JPAY00000000000000000002",
                monthMillis: SubscriptionFixtures.mayMillis
            ),
        ]

        let model = model()
        await model.refresh()

        XCTAssertEqual(
            model.state.payments.map { String(describing: $0.periodMonth) },
            ["2026-06-01", "2026-05-01", "2026-04-01"]
        )
        XCTAssertFalse(model.state.isEmpty)
    }

    /// The header wants the vehicle and the standing monthly fare, and the statement carries neither —
    /// hence the second read.
    @MainActor
    func testTheHeaderCostsASecondReadBecauseAStatementCarriesNoFare() async {
        repository.held = [SubscriptionFixtures.paidSubscription()]
        repository.payments = [
            SubscriptionFixtures.payment(
                method: SubscriptionPayMethod.onlineTransfer,
                status: SubscriptionPaymentStatus.pendingVerification
            ),
        ]

        let model = model()
        await model.refresh()

        XCTAssertEqual(model.state.fare?.amountMinor, SubscriptionFixtures.monthlyFareMinor)
        XCTAssertEqual(model.state.subscription?.vehicleId, SubscriptionFixtures.vehicleId)
        XCTAssertEqual(repository.paymentReads, [SubscriptionFixtures.subscriptionId])
    }

    /// **The four statuses the wireframe prints all survive the round trip**, and the two that are not
    /// money the owner has look like it: an `initiated` hand-off nobody completed is *Not paid*.
    func testTheStatusPillSaysWhatTheOwnerActuallyHas() {
        let paid = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.lankaqrScan,
            status: SubscriptionPaymentStatus.paid
        )
        let cash = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.cash,
            status: SubscriptionPaymentStatus.paid
        )
        let pending = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.onlineTransfer,
            status: SubscriptionPaymentStatus.pendingVerification
        )
        let abandoned = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.lankaqrDeeplink,
            status: SubscriptionPaymentStatus.initiated
        )
        let failed = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.lankaqrDeeplink,
            status: SubscriptionPaymentStatus.failed
        )

        XCTAssertEqual(SubscriptionLabels.paymentPill(paid).titleKey, "subscription_status_paid")
        XCTAssertEqual(SubscriptionLabels.paymentPill(cash).titleKey, "subscription_status_paid_cash")
        XCTAssertEqual(SubscriptionLabels.paymentPill(pending).titleKey, "subscription_status_pending")
        XCTAssertEqual(SubscriptionLabels.paymentPill(abandoned).titleKey, "subscription_status_unpaid")
        XCTAssertEqual(SubscriptionLabels.paymentPill(failed).titleKey, "subscription_status_unpaid")
    }

    /// A subscription with no payments yet is empty rather than still loading.
    @MainActor
    func testASubscriptionWithNoPaymentsYetIsEmptyRatherThanStillLoading() async {
        let model = model()
        await model.refresh()
        XCTAssertTrue(model.state.isEmpty)
    }
}

// MARK: - The Colombo clock and the signed link

/// Every date on these four screens is Asia/Colombo (D-38), and a `BusinessDate` is one the **server**
/// already derived there.
final class SubscriptionDateTests: XCTestCase {

    /// `2026-07-06` → `6 Jul`, and `2026-06-01` → `Jun 2026`. Read and written in Colombo, so the
    /// round trip lands on the day it started on — a formatter left on the handset's zone answers a
    /// different day for five and a half hours out of every twenty-four.
    func testABusinessDateIsPrintedOnTheDayTheServerMeant() {
        let nextDue = SubscriptionFixtures.businessDate(SubscriptionFixtures.nextDueMillis)
        let june = SubscriptionFixtures.businessDate(SubscriptionFixtures.juneMillis)

        XCTAssertEqual(String(describing: nextDue), "2026-07-06")
        XCTAssertEqual(TripLabels.dayMonth(nextDue), "6 Jul")
        XCTAssertEqual(TripLabels.monthYear(june), "Jun 2026")
    }

    /// `YYYY-MM-DD` sorts lexicographically in calendar order, which is why the two screens that rank
    /// months do it on the ISO text rather than on a Kotlin `compareTo` the bridge does not carry.
    func testPeriodsRankInCalendarOrder() {
        let april = SubscriptionFixtures.businessDate(SubscriptionFixtures.aprilMillis)
        let june = SubscriptionFixtures.businessDate(SubscriptionFixtures.juneMillis)

        XCTAssertTrue(SubscriptionPeriod.isBefore(april, june))
        XCTAssertFalse(SubscriptionPeriod.isBefore(june, april))
        XCTAssertFalse(SubscriptionPeriod.isBefore(june, june))
    }

    /// A `paidAt` is an **instant**, and the day it falls on is a Colombo day: 19:00Z on the 17th is
    /// already the 18th in Colombo, which is the case a UTC formatter gets wrong on the *date* rather
    /// than only on the hour.
    func testAPaidAtIsReadOnTheColomboDay() {
        let payment = SubscriptionFixtures.payment(
            method: SubscriptionPayMethod.lankaqrScan,
            status: SubscriptionPaymentStatus.paid
        )
        XCTAssertTrue(
            SubscriptionLabels.paymentLine(payment).hasPrefix("6 Jun"),
            "a payment settled at 10:15 Colombo on 6 June is a 6 June row"
        )
    }
}

/// AL-49's signed link, taken apart — and the shapes that must answer nothing.
///
/// The link **is** the credential (`security: []` on `GET /v1/mode-b/files/{kind}/{id}`), so what
/// matters is that a malformed or foreign one produces a `nil` the pay sheet can draw around rather
/// than an error that reads as a failed payment.
final class SignedFileLinkTests: XCTestCase {

    private let profile = SubscriptionFixtures.payoutProfileId

    func testALankaQrLinkGivesBackTheFourValuesTheClientNeeds() {
        let link = SignedFileLink.parse(
            "https://api.mageride.lk/v1/mode-b/files/lankaqr/\(profile)?expires=1780000000&signature=abc123"
        )

        XCTAssertEqual(
            link,
            SignedFileLink(
                kind: ModeBFileKind.lankaqr,
                id: profile,
                expires: 1_780_000_000,
                signature: "abc123"
            )
        )
    }

    /// The other half of the route. The pay sheet never fetches one, but the kind is what
    /// ``ApiSubscriptionRepository/ownerLankaQr(link:)`` filters on — so a slip link handed to it by a
    /// mis-shaped `payTo` must be distinguishable rather than fetched as a QR.
    func testASlipLinkParsesTooAndKeepsItsKind() {
        let link = SignedFileLink.parse("/v1/mode-b/files/slips/\(profile)?expires=1&signature=z")
        XCTAssertEqual(link?.kind, ModeBFileKind.slips)
    }

    /// The call is re-issued against the app's own configured gateway, so the host in the link is
    /// never followed — which is what stops a minted URL redirecting this app anywhere.
    func testTheOriginIsDiscardedAndARelativeLinkWorks() {
        XCTAssertEqual(
            SignedFileLink.parse("https://internal.example/v1/mode-b/files/lankaqr/\(profile)?expires=9&signature=s"),
            SignedFileLink.parse("/v1/mode-b/files/lankaqr/\(profile)?expires=9&signature=s")
        )
    }

    func testAnythingThatIsNotASignedModeBFileLinkIsNil() {
        XCTAssertNil(SignedFileLink.parse(nil), "no link at all")
        XCTAssertNil(SignedFileLink.parse(""), "empty")
        XCTAssertNil(SignedFileLink.parse("https://example.com/logo.png"), "some other URL")
        XCTAssertNil(
            SignedFileLink.parse("/v1/mode-b/files/passport/\(profile)?expires=1&signature=s"),
            "a kind this build does not know"
        )
        XCTAssertNil(SignedFileLink.parse("/v1/mode-b/files/lankaqr/\(profile)?signature=s"), "no expiry")
        XCTAssertNil(SignedFileLink.parse("/v1/mode-b/files/lankaqr/\(profile)?expires=1"), "no signature")
        XCTAssertNil(SignedFileLink.parse("/v1/mode-b/files/lankaqr/?expires=1&signature=s"), "no id")
        XCTAssertNil(
            SignedFileLink.parse("/v1/mode-b/files/lankaqr/\(profile)?expires=soon&signature=s"),
            "an expiry that is not a number"
        )
    }
}

// MARK: - The code table

/// D-26 — a failure is copy this app resolved from a kebab code, never a `ProblemDetails` string.
final class ModeBErrorsTests: XCTestCase {

    /// **Every code this cluster's operations declare has copy**, and every one of them is a key the
    /// three `.strings` files carry — `LocalizationTests` is what checks the second half.
    ///
    /// A `MageRideError` cannot be constructed from Swift without the Kotlin initialiser (the C095
    /// finding), so what is asserted here is the *table*: the keys it can produce, against the ones
    /// declared. AL-49's `409 payout-profile-not-verified` is the one that matters most — it is the
    /// fleet's own failure, and the useful answer is *"pay your collector"* rather than *"something
    /// went wrong"*.
    func testTheErrorTableCoversTheCodesThisClusterCanSee() {
        let keys = [
            "error_payout_not_verified", "error_vehicle_not_found", "error_request_already_open",
            "error_subscription_not_found", "error_not_your_subscription", "error_slip_too_large",
            "error_validation_failed", "error_gateway", "error_dependency_unavailable",
            "error_offline", "error_generic",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
    }

    /// A failure with nothing behind it resolves to the generic message rather than to nothing.
    func testAnUnrecognisedFailureIsGeneric() {
        XCTAssertEqual(ModeBErrors.messageKey(for: SubscriptionFakeError.unreachable), "error_generic")
    }
}
