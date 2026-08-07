import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-024's rules** — the exact value on both legs, the affordability check against the
/// *spendable* balance, and an inbox that is read rather than pushed.
@MainActor
final class CreditTransferModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var transfers: FakeCreditTransferRepository!
    private var wallet: FakeWalletRepository!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        transfers = FakeCreditTransferRepository()
        wallet = FakeWalletRepository()
    }

    private func makeModel() -> CreditTransferModel {
        CreditTransferModel(identity: identity, transfers: transfers, wallet: wallet)
    }

    private func standing(availableMinor: Int64, balanceMinor: Int64? = nil) -> WalletStanding {
        WalletStanding.companion.of(
            wallet: driverWallet(balanceMinor: balanceMinor ?? availableMinor, availableMinor: availableMinor)
        )
    }

    // MARK: - The exact value

    /// **The DoD line.** A transfer of Rs 500 shows Rs 500 debited and Rs 500 received, at every amount,
    /// with no fee leg anywhere (AL-01).
    func testBothLegsAreTheSameFigureAtEveryAmount() async {
        let model = makeModel()

        for rupees in ["1", "500", "1000", "999999999"] {
            model.onAmountChange(rupees)
            XCTAssertEqual(model.state.debitedMinor, model.state.creditedMinor, "Rs \(rupees) moved a fee leg")
            XCTAssertEqual(model.state.debitedMinor, model.state.amountMinor)
        }
    }

    func testTheSendIsByDriverIdAndCarriesTheExactAmount() async {
        wallet.balances = [standing(availableMinor: 200_000)]
        let model = makeModel()
        await model.refresh()
        model.onRecipientIdChange(testHolderId)
        model.onAmountChange("500")

        await model.send()

        XCTAssertEqual(transfers.sends.count, 1)
        XCTAssertEqual(transfers.sends.first?.recipientDriverId, testHolderId)
        XCTAssertEqual(transfers.sends.first?.amountMinor, 50_000)
        XCTAssertEqual(model.state.sent?.amountMinor, 50_000)
        XCTAssertEqual(model.state.recipientId, "", "the form is cleared so a second tap is a second decision")
    }

    // MARK: - Affordability

    /// A driver holding Rs 300 who owes Rs 200 can send Rs 100; offering them Rs 250 would be describing
    /// money they do not have.
    func testAffordabilityIsCheckedAgainstTheSpendableBalanceAndNotTheHeadline() async {
        wallet.balances = [standing(availableMinor: 10_000, balanceMinor: 30_000)]
        let model = makeModel()
        await model.refresh()
        model.onRecipientIdChange(testHolderId)

        model.onAmountChange("250")
        XCTAssertEqual(model.rejectionForSend(), CreditTransferRejection.insufficientBalance)
        XCTAssertFalse(model.canSend)

        model.onAmountChange("100")
        XCTAssertNil(model.rejectionForSend())
        XCTAssertTrue(model.canSend)
    }

    /// **`CreditTransferIntent`'s `init` would terminate the process here**, not throw, so the two
    /// guards are answered before one is built. This test is the reason they are not folded in.
    func testASelfTransferIsRefusedBeforeAnIntentIsBuilt() async {
        wallet.balances = [standing(availableMinor: 200_000)]
        let model = makeModel()
        await model.refresh()
        model.onRecipientIdChange(testDriverId)
        model.onAmountChange("500")

        XCTAssertEqual(model.rejectionForSend(), CreditTransferRejection.selfTransfer)
        XCTAssertFalse(model.canSend)

        await model.send()
        XCTAssertTrue(transfers.sends.isEmpty)
    }

    func testANonPositiveAmountIsRefusedBeforeAnIntentIsBuilt() async {
        wallet.balances = [standing(availableMinor: 200_000)]
        let model = makeModel()
        await model.refresh()
        model.onRecipientIdChange(testHolderId)

        model.onAmountChange("0")

        XCTAssertEqual(model.rejectionForSend(), CreditTransferRejection.nonPositiveAmount)
        XCTAssertFalse(model.canSend)
    }

    func testAMalformedRecipientIdIsAnsweredAtTheKeyboard() async {
        wallet.balances = [standing(availableMinor: 200_000)]
        let model = makeModel()
        await model.refresh()
        model.onAmountChange("500")

        model.onRecipientIdChange("DRV-22011")

        XCTAssertTrue(model.state.isRecipientIdRejected, "there is no DRV-22011")
        XCTAssertFalse(model.canSend)
    }

    func testABlankFieldIsNotYetRatherThanWrong() {
        let model = makeModel()

        XCTAssertFalse(model.state.isRecipientIdRejected)
        XCTAssertFalse(model.canSend)
    }

    // MARK: - The inbox

    /// D2' says the requests arrive via APNs and **no such notification type exists** — the list is
    /// read on open, and a list that only filled on a push would be permanently empty.
    func testTheInboxIsReadOnOpen() async {
        transfers.pendingRows = [transferRow()]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.incoming.count, 1)
        XCTAssertEqual(wallet.balanceReads, 1)
        XCTAssertEqual(transfers.historyReads, [nil], "both directions, because the parameter's default is all")
    }

    /// The row leaves the inbox on the **server's answer**, not on the tap: a `409` means somebody
    /// already answered it and a `402` means the balance moved underneath.
    func testAnApprovalMovesTheRowIntoTheHistoryAndReReadsTheBalance() async {
        transfers.pendingRows = [transferRow()]
        wallet.balances = [standing(availableMinor: 200_000), standing(availableMinor: 100_000)]
        let model = makeModel()
        await model.refresh()

        await model.approve(transferId: testTransferId)

        XCTAssertEqual(transfers.approvals, [testTransferId])
        XCTAssertTrue(model.state.incoming.isEmpty)
        XCTAssertEqual(model.state.history.first?.transferId, testTransferId)
        XCTAssertEqual(model.state.standing?.available.amountMinor, 100_000, "the next decision uses the new figure")
    }

    /// A rejected request moved no money, and putting a REJECTED row into the history list would be
    /// wrong on both counts.
    func testADeclineLeavesTheHistoryAlone() async {
        transfers.pendingRows = [transferRow()]
        let model = makeModel()
        await model.refresh()

        await model.reject(transferId: testTransferId)

        XCTAssertEqual(transfers.rejections, [testTransferId])
        XCTAssertTrue(model.state.incoming.isEmpty)
        XCTAssertTrue(model.state.history.isEmpty)
    }

    /// A `402` at approval time is the holder's balance having moved, and the request is still there to
    /// be looked at.
    func testARefusedApprovalKeepsTheRowInTheInbox() async {
        transfers.pendingRows = [transferRow()]
        transfers.nextDecisionFailure = TestWalletFailure()
        let model = makeModel()
        await model.refresh()

        await model.approve(transferId: testTransferId)

        XCTAssertEqual(model.state.incoming.count, 1)
        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertNil(model.state.busyTransferId)
    }

    /// The card swaps its two `textlink`s for a spinner while the decision is out, and puts them back
    /// whichever way the server answered — including the refusal above, where the row stays.
    func testTheBusyRowIsClearedWhicheverWayTheServerAnswers() async {
        transfers.pendingRows = [transferRow()]
        let model = makeModel()
        await model.refresh()

        await model.approve(transferId: testTransferId)
        XCTAssertNil(model.state.busyTransferId)

        transfers.nextDecisionFailure = TestWalletFailure()
        await model.reject(transferId: testTransferId)
        XCTAssertNil(model.state.busyTransferId)
    }

    // MARK: -

    func testAFailedReadBecomesCopy() async {
        transfers.nextPendingFailure = TestWalletFailure()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)
        model.dismissError()
        XCTAssertNil(model.state.errorKey)
    }

    func testNothingIsReadWithoutASession() async {
        identity.driverId = nil
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(wallet.balanceReads, 0)
    }
}

/// **SCR-DI-023's rules** — the pull half, which moves nothing and checks no balance.
@MainActor
final class RequestCreditModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var transfers: FakeCreditTransferRepository!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        transfers = FakeCreditTransferRepository()
    }

    private func makeModel() -> RequestCreditModel {
        RequestCreditModel(identity: identity, transfers: transfers)
    }

    /// A request costs the requester nothing and the holder's balance is not this driver's to see;
    /// `402 insufficient-wallet` is raised at *approval* time, on the holder's screen.
    func testARequestIsRaisedWithNoBalanceCheckOfItsOwn() async {
        let model = makeModel()
        model.onHolderIdChange(testHolderId)
        model.onAmountChange("1000")

        XCTAssertTrue(model.state.canRequest)
        await model.request()

        XCTAssertEqual(transfers.requests.count, 1)
        XCTAssertEqual(transfers.requests.first?.holderDriverId, testHolderId)
        XCTAssertEqual(transfers.requests.first?.amountMinor, 100_000)
    }

    /// The form is cleared because the request now lives in the list below it; leaving it filled invites
    /// a second identical ask on the same holder.
    func testASuccessfulRequestClearsTheFormAndJoinsTheList() async {
        let model = makeModel()
        model.onHolderIdChange(testHolderId)
        model.onAmountChange("1000")

        await model.request()

        XCTAssertEqual(model.state.holderId, "")
        XCTAssertEqual(model.state.amount, "")
        XCTAssertEqual(model.state.outgoing.count, 1)
        XCTAssertEqual(model.state.justRequested?.amountMinor, 100_000)
    }

    /// An approved request is money already in the wallet and belongs to the history screen; only a
    /// `PENDING` row is *"awaiting driver approval"*.
    func testOnlyPendingRowsAreListedAsOutstanding() async {
        transfers.historyRows = [
            transferRow(transferId: "pending", status: TransferStatus.pending),
            transferRow(transferId: "done", status: TransferStatus.approved),
            transferRow(transferId: "declined", status: TransferStatus.rejected),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.outgoing.map(\.transferId), ["pending"])
        XCTAssertEqual(transfers.historyReads, [TransferDirectionFilter.received])
    }

    func testTheCtaIsDeadUntilBothFieldsAreValid() {
        let model = makeModel()

        XCTAssertFalse(model.state.canRequest)
        model.onHolderIdChange(testHolderId)
        XCTAssertFalse(model.state.canRequest, "no amount yet")
        model.onAmountChange("1000")
        XCTAssertTrue(model.state.canRequest)
        model.onHolderIdChange("nope")
        XCTAssertFalse(model.state.canRequest)
    }

    func testAFailedRequestBecomesCopyAndLeavesTheFormAlone() async {
        transfers.nextRequestFailure = TestWalletFailure()
        let model = makeModel()
        model.onHolderIdChange(testHolderId)
        model.onAmountChange("1000")

        await model.request()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.holderId, testHolderId, "the driver retries rather than retypes")
        XCTAssertFalse(model.state.isSubmitting)
    }
}
