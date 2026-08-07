import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-022's rules** — the voucher that is a purchase and not a discounted top-up, the three
/// rails and no fourth, AL-15's fallback, and D6' §7.1's pending window.
@MainActor
final class TopUpModelTests: XCTestCase {

    private var topUps: FakeTopUpRepository!
    private var handoff: FakePaymentHandoff!

    override func setUp() {
        super.setUp()
        topUps = FakeTopUpRepository()
        handoff = FakePaymentHandoff()
    }

    /// The poll runs on a zero interval so a ninety-second window is a loop rather than a wait: the
    /// rule under test is *"how many reads before it gives up"*, not *"how long does three seconds
    /// take"*. Same argument ``JobBoardModel``'s injected clock makes.
    private func makeModel(pollSeconds: TimeInterval = 0, pendingWindowSeconds: TimeInterval = 9) -> TopUpModel {
        TopUpModel(
            topUps: topUps,
            handoff: handoff,
            pollSeconds: pollSeconds,
            pendingWindowSeconds: pendingWindowSeconds
        )
    }

    // MARK: - A voucher is a purchase

    /// **The DoD line, and the one that pays twice if it is wrong.** A Rs 1,000 voucher at 10% prices
    /// at Rs 900 and credits Rs 1,000 — through `POST /v1/vouchers/purchase`, never through a top-up of
    /// the discounted price, which would credit Rs 900 on the webhook *and* Rs 1,000 on the purchase.
    func testARs1000VoucherAt10PercentPricesAt900AndCredits1000() async {
        topUps.purchase = voucherPurchase(denominationMinor: 100_000, discountBps: 1_000, paidMinor: 90_000)
        let model = makeModel()
        await model.refresh()

        model.selectVoucher(denominationMinor: 100_000)

        XCTAssertEqual(model.state.payableMinor, 90_000)
        XCTAssertEqual(model.state.creditedMinor, 100_000)
        XCTAssertEqual(model.state.amount, "1000", "the field shows the face value, which is what is bought")

        await model.pay()

        XCTAssertEqual(topUps.purchases.map(\.denominationMinor), [100_000])
        XCTAssertTrue(topUps.topUps.isEmpty, "topping up the discounted price would credit Rs 1,900")
        XCTAssertEqual(model.state.receipt?.paidMinor, 90_000)
        XCTAssertEqual(model.state.receipt?.creditedMinor, 100_000)
    }

    /// subscription-svc posts the credit on the gateway's confirmation and exposes no read to poll, so
    /// the receipt says the credit is on its way rather than implying a balance that is not there.
    func testAVoucherReceiptIsNotSettled() async {
        let model = makeModel()
        await model.refresh()
        model.selectVoucher(denominationMinor: 100_000)

        await model.pay()

        XCTAssertEqual(model.state.receipt?.isSettled, false)
        XCTAssertNil(model.state.pending, "there is nothing to poll")
    }

    /// A voucher is a fixed denomination; an amount beside a highlighted tile that no longer describes
    /// it is how a driver pays for one thing believing they bought another.
    func testTypingAnAmountClearsTheSelectedTile() async {
        let model = makeModel()
        await model.refresh()
        model.selectVoucher(denominationMinor: 200_000)

        model.onAmountChange("300")

        XCTAssertNil(model.state.voucherDenominationMinor)
        XCTAssertEqual(model.state.payableMinor, 30_000, "a plain top-up of Rs 300")
    }

    func testTappingASelectedTileAgainLeavesTheFigureBehindAsAPlainTopUp() async {
        let model = makeModel()
        await model.refresh()
        model.selectVoucher(denominationMinor: 200_000)

        model.selectVoucher(denominationMinor: 200_000)

        XCTAssertNil(model.state.voucherDenominationMinor)
        XCTAssertEqual(model.state.payableMinor, 200_000, "the face value, at face value")
        XCTAssertEqual(model.state.creditedMinor, 200_000)
    }

    /// **A voucher purchase raises two presentations at once**, and this is the state SCR-DI-022's one
    /// ranked sheet exists for: on Android they are two stacked dialogs, and SwiftUI would silently
    /// drop the second. The model is right to set both — the browser is how the driver pays and the
    /// receipt is what they read afterwards — so the ranking is the screen's job, not this class's.
    func testAVoucherOnOnePayRaisesBothTheCheckoutAndTheReceipt() async {
        let model = makeModel()
        await model.refresh()
        model.selectVoucher(denominationMinor: 100_000)

        await model.pay()

        XCTAssertNotNil(model.state.onepayUrl)
        XCTAssertNotNil(model.state.receipt)
    }

    /// `VoucherCatalogue` ships no default ladder: a client that has not read the tiers has nothing to
    /// sell, which is the honest answer rather than a rate no admin set.
    func testAWithdrawnDenominationIsNotOnSale() async {
        topUps.tiers = [voucherTier(denominationMinor: 100_000, discountBps: 1_000, active: false)]
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.vouchers.isEmpty)
    }

    /// **A Kotlin `require` is a caught exception on Android and a terminated process here**, so the
    /// tier list is put through `VoucherCatalogue`'s own four rules before it is handed over and the
    /// refusal becomes copy. Each case below is one of those `require`s.
    func testAMalformedTierTableIsRefusedBeforeKotlinSeesIt() {
        XCTAssertFalse(
            ApiTopUpRepository.isWellFormed([
                voucherTier(denominationMinor: 100_000, discountBps: 1_000),
                voucherTier(denominationMinor: 100_000, discountBps: 1_200),
            ]),
            "two tiers for one denomination"
        )
        XCTAssertFalse(ApiTopUpRepository.isWellFormed([voucherTier(denominationMinor: 0, discountBps: 1_000)]))
        XCTAssertFalse(ApiTopUpRepository.isWellFormed([voucherTier(denominationMinor: 100_000, discountBps: -1)]))
        XCTAssertFalse(ApiTopUpRepository.isWellFormed([voucherTier(denominationMinor: 100_000, discountBps: 10_001)]))
        XCTAssertTrue(ApiTopUpRepository.isWellFormed(testVoucherTiers))
        XCTAssertTrue(ApiTopUpRepository.isWellFormed([]), "an empty ladder is nothing on sale, not a fault")
    }

    // MARK: - The two rails

    func testAPlainTopUpGoesToWalletSvcOnTheChosenRail() async {
        let model = makeModel()
        await model.refresh()
        model.select(method: TopupMethod.onepayWallet)
        model.onAmountChange("2000")

        await model.pay()

        XCTAssertEqual(topUps.topUps.map(\.amountMinor), [200_000])
        XCTAssertEqual(topUps.topUps.first?.method, TopupMethod.onepayWallet)
        XCTAssertTrue(topUps.purchases.isEmpty)
    }

    /// **Δ iOS** — the hosted page is an `SFSafariViewController` the screen presents, so the model
    /// raises a URL rather than asking a system to leave the app. There is no "no browser" failure.
    func testOnePayRaisesTheCheckoutRatherThanLeavingTheApp() async {
        topUps.startedTopup = topup(redirectUrl: "https://checkout.onepay.lk/session/abc")
        let model = makeModel()
        await model.refresh()
        model.onAmountChange("2000")

        await model.pay()

        XCTAssertEqual(model.state.onepayUrl?.url.absoluteString, "https://checkout.onepay.lk/session/abc")
        XCTAssertTrue(handoff.openedUrls.isEmpty, "nothing left the app")
        XCTAssertNotNil(model.state.pending, "a session the driver reached a gateway for is polled")
    }

    /// AL-15's primary path: the *"Pay"* link into the driver's own bank app.
    func testLankaQrOpensTheBankAppWhenOneClaimsTheLink() async {
        topUps.startedTopup = topup(
            redirectUrl: nil,
            paymentLink: "https://pay.bank.lk/lankaqr/abc",
            qrPayload: "00020101021..."
        )
        handoff.opens = true
        let model = makeModel()
        await model.refresh()
        model.select(method: TopupMethod.lankaqr)
        model.onAmountChange("2000")

        await model.pay()

        XCTAssertEqual(handoff.openedUrls, ["https://pay.bank.lk/lankaqr/abc"])
        XCTAssertNil(model.state.fallbackQr, "the code is a fallback, not the first offer")
    }

    /// AL-15's fallback, and it is **tried, not asked**: `canOpenURL` cannot answer for a scheme that
    /// is the issuing bank's, so the link is opened and the failure is what raises the code.
    func testTheCodeAppearsOnlyWhenNoBankAppClaimedTheLink() async {
        topUps.startedTopup = topup(
            redirectUrl: nil,
            paymentLink: "https://pay.bank.lk/lankaqr/abc",
            qrPayload: "00020101021..."
        )
        handoff.opens = false
        let model = makeModel()
        await model.refresh()
        model.select(method: TopupMethod.lankaqr)
        model.onAmountChange("2000")

        await model.pay()

        XCTAssertEqual(model.state.fallbackQr?.payload, "00020101021...")
        XCTAssertNotNil(model.state.pending, "a scanned code still resolves through the webhook")
    }

    /// `VoucherPurchase` declares `redirectUrl` and `qrPayload` and **no** `paymentLink`, so the
    /// LankaQR arm of a voucher is the fallback by construction. The C073 spec gap, carried forward.
    func testAVoucherOnLankaQrIsAlwaysTheFallbackCode() async {
        topUps.purchase = voucherPurchase(redirectUrl: nil, qrPayload: "00020101021...")
        let model = makeModel()
        await model.refresh()
        model.select(method: TopupMethod.lankaqr)
        model.selectVoucher(denominationMinor: 100_000)

        await model.pay()

        XCTAssertEqual(model.state.fallbackQr?.payload, "00020101021...")
        XCTAssertTrue(handoff.openedUrls.isEmpty, "there is no deep link on this response to open")
    }

    func testAGatewayThatOfferedNothingIsCopyRatherThanASilentNoOp() async {
        topUps.startedTopup = topup(redirectUrl: nil)
        let model = makeModel()
        await model.refresh()
        model.onAmountChange("2000")

        await model.pay()

        XCTAssertEqual(model.state.errorKey, "error_gateway_unreachable")
        XCTAssertNil(model.state.pending, "nothing to poll — the driver never reached a gateway")
    }

    // MARK: - The pending window

    func testASucceededSessionSettlesIntoAReceipt() async {
        topUps.startedTopup = topup(amountMinor: 200_000)
        topUps.polledStates = [topup(state: TopupState.succeeded, amountMinor: 200_000)]
        let model = makeModel()
        await model.refresh()
        model.onAmountChange("2000")
        await model.pay()

        await model.onCheckoutDismissed()

        XCTAssertEqual(model.state.receipt, TopUpReceipt(paidMinor: 200_000, creditedMinor: 200_000, isSettled: true))
        XCTAssertNil(model.state.pending)
        XCTAssertNil(model.state.onepayUrl)
    }

    /// The wireframe's *"Failed → retry"*: the form keeps the figure so the driver taps once rather
    /// than entering the amount again.
    func testAFailedSessionKeepsTheAmountInTheForm() async {
        topUps.startedTopup = topup(amountMinor: 200_000)
        topUps.polledStates = [topup(state: TopupState.failed)]
        let model = makeModel()
        await model.refresh()
        model.onAmountChange("2000")
        await model.pay()
        model.onAmountChange("")

        await model.onCheckoutDismissed()

        XCTAssertEqual(model.state.errorKey, "wallet_topup_failed")
        XCTAssertNil(model.state.receipt)
        XCTAssertEqual(model.state.amount, "2000")
    }

    /// **A timed-out window is not a failure.** The webhook may simply be late, and telling a driver who
    /// has paid that nothing happened is worse than saying the credit is on its way.
    func testAWindowThatClosesOnAPendingSessionIsNotAnError() async {
        topUps.polledStates = [topup(state: TopupState.pending)]
        let model = makeModel(pollSeconds: 0, pendingWindowSeconds: 3)
        await model.refresh()
        model.onAmountChange("2000")
        await model.pay()

        await model.onCheckoutDismissed()

        XCTAssertEqual(model.state.pending?.hasTimedOut, true)
        XCTAssertNil(model.state.errorKey)
        XCTAssertNil(model.state.receipt)
        XCTAssertFalse(model.state.isAwaitingGateway)
    }

    func testAClosedWindowIsNotPolledAgain() async {
        topUps.polledStates = [topup(state: TopupState.pending)]
        let model = makeModel(pollSeconds: 0, pendingWindowSeconds: 1)
        await model.refresh()
        model.onAmountChange("2000")
        await model.pay()
        await model.onCheckoutDismissed()
        let reads = topUps.pollReads

        await model.onCheckoutDismissed()

        XCTAssertEqual(topUps.pollReads, reads)
    }

    // MARK: - The CTA

    func testTheCtaIsDeadWithoutAPositiveAmount() async {
        let model = makeModel()
        await model.refresh()

        XCTAssertFalse(model.state.canPay)
        model.onAmountChange("0")
        XCTAssertFalse(model.state.canPay)
        model.onAmountChange("500")
        XCTAssertTrue(model.state.canPay)
    }

    func testDismissingTheReceiptClearsTheFormForAnotherTopUp() async {
        let model = makeModel()
        await model.refresh()
        model.selectVoucher(denominationMinor: 100_000)
        await model.pay()

        model.dismissReceipt()

        XCTAssertNil(model.state.receipt)
        XCTAssertEqual(model.state.amount, "")
        XCTAssertNil(model.state.voucherDenominationMinor)
    }

    func testAFailedTierReadBecomesCopy() async {
        topUps.nextTiersFailure = TestWalletFailure()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)
        model.dismissError()
        XCTAssertNil(model.state.errorKey)
    }
}
