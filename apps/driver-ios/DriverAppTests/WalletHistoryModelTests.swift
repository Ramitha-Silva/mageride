import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-025's rules** — three filters of two different kinds, and a statement that covers the
/// range on screen rather than the rows on screen.
@MainActor
final class WalletHistoryModelTests: XCTestCase {

    private var identity: FakeDriverIdentity!
    private var wallet: FakeWalletRepository!
    private var exporter: FakeStatementExporter!

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        wallet = FakeWalletRepository()
        exporter = FakeStatementExporter()
    }

    private func makeModel() -> WalletHistoryModel {
        WalletHistoryModel(identity: identity, wallet: wallet, exporter: exporter)
    }

    private func ledger() -> [WalletTransaction] {
        [
            walletTransaction(entryId: "fee", kind: LedgerKinds.dailyFee, amountMinor: -10_000),
            walletTransaction(entryId: "topup", kind: LedgerKinds.topup, amountMinor: 200_000),
            walletTransaction(entryId: "voucher", kind: LedgerKinds.voucherPurchase, amountMinor: 100_000),
            walletTransaction(entryId: "transfer", kind: LedgerKinds.driverTransfer, amountMinor: 100_000),
            walletTransaction(entryId: "fare", kind: LedgerKinds.tripPayment, amountMinor: 48_000),
        ]
    }

    // MARK: - The chips

    /// The chips run on the device over the page already read, because the route takes a date range and
    /// no `kind`. Switching one must not re-hit the API.
    func testAChipFiltersTheRowsAlreadyReadAndReadsNothing() async {
        wallet.lines = ledger()
        let model = makeModel()
        await model.refresh()
        let reads = wallet.transactionReads.count

        model.select(filter: .fees)

        XCTAssertEqual(model.state.visible.map(\.entryId), ["fee"])
        XCTAssertEqual(wallet.transactionReads.count, reads)
    }

    /// US-9.19 credits the buyer's own wallet at purchase, so from the ledger's side a voucher is a
    /// top-up that cost less than it credited — and it belongs under the same chip.
    func testAVoucherIsATopUpRatherThanAChipOfItsOwn() async {
        wallet.lines = ledger()
        let model = makeModel()
        await model.refresh()

        model.select(filter: .topUps)

        XCTAssertEqual(Set(model.state.visible.map(\.entryId)), ["topup", "voucher"])
    }

    func testTheAllChipKeepsEverythingIncludingAKindThisBuildHasNotHeardOf() async {
        wallet.lines = ledger() + [walletTransaction(entryId: "new", kind: "some_future_kind")]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.visible.count, 6)
        XCTAssertEqual(LedgerKinds.labelKey(for: "some_future_kind"), "wallet_kind_other")
    }

    // MARK: - The search (Δ iOS)

    /// The `.searchable` field matches the row's own **localised name** and its reference. It matches
    /// neither the amount nor the raw machine key: typing `100` would otherwise put every `Rs 1,000`
    /// line under a search for a hundred rupees.
    func testTheSearchMatchesTheRowsNameAndItsReference() async {
        wallet.lines = [
            walletTransaction(entryId: "fee", kind: LedgerKinds.dailyFee),
            walletTransaction(entryId: "topup", kind: LedgerKinds.topup, amountMinor: 100_000, reference: "OP-99"),
        ]
        let model = makeModel()
        await model.refresh()

        model.onQueryChange("daily")
        XCTAssertEqual(model.state.visible.map(\.entryId), ["fee"], "the localised label")

        model.onQueryChange("op-99")
        XCTAssertEqual(model.state.visible.map(\.entryId), ["topup"], "case-insensitive, on the reference")

        model.onQueryChange("1000")
        XCTAssertTrue(model.state.visible.isEmpty, "an amount is not searched")

        model.onQueryChange("   ")
        XCTAssertEqual(model.state.visible.count, 2, "whitespace is not a query")
    }

    func testTheChipAndTheSearchCompose() async {
        wallet.lines = ledger()
        let model = makeModel()
        await model.refresh()

        model.select(filter: .topUps)
        model.onQueryChange("voucher")

        XCTAssertEqual(model.state.visible.map(\.entryId), ["voucher"])
    }

    func testAReadThatKeptNothingIsTheEmptyState() async {
        wallet.lines = ledger()
        let model = makeModel()
        await model.refresh()

        model.onQueryChange("nothing matches this")

        XCTAssertTrue(model.state.isEmpty)
    }

    // MARK: - The date range

    /// The range **is** the server's filter, and it is evaluated as Colombo business dates (D-13, D-38).
    func testTheRangeIsSentToTheServerAndReReadsTheLedger() async {
        let today = IosBusinessDateKt.colomboBusinessDateNow()
        let model = makeModel()
        await model.refresh()

        await model.setRange(from: today, to: today)

        XCTAssertEqual(wallet.transactionReads.count, 2)
        XCTAssertEqual(wallet.transactionReads.last?.from, today)
        XCTAssertEqual(wallet.transactionReads.last?.to, today)
        XCTAssertTrue(model.state.hasRange)
    }

    func testClearingTheRangeGoesBackToEverythingTheServerSends() async {
        let today = IosBusinessDateKt.colomboBusinessDateNow()
        let model = makeModel()
        await model.setRange(from: today, to: today)

        await model.setRange(from: nil, to: nil)

        XCTAssertFalse(model.state.hasRange)
        XCTAssertNil(wallet.transactionReads.last?.from)
    }

    // MARK: - The statement

    /// **The chip and the search are deliberately not applied.** A statement is evidence of what the
    /// ledger did, and one that quietly omitted the filtered-out rows would not reconcile with the
    /// balance printed on it.
    func testTheStatementCoversTheRangeAndIgnoresTheChipAndTheSearch() async {
        let today = IosBusinessDateKt.colomboBusinessDateNow()
        wallet.lines = ledger()
        let model = makeModel()
        await model.setRange(from: today, to: today)
        model.select(filter: .fees)
        model.onQueryChange("daily")

        await model.export(.csv)

        XCTAssertEqual(wallet.statementReads.count, 1)
        XCTAssertEqual(wallet.statementReads.first?.format, .csv)
        XCTAssertEqual(wallet.statementReads.first?.from, today)
        XCTAssertEqual(wallet.statementReads.first?.to, today)
    }

    /// A file name is data, not copy — the same rule `Rs` and `+94` follow — and `all` stands in for an
    /// open bound so two downloads of different ranges never overwrite each other.
    func testTheFileNameNamesTheRangeAndTheFormat() async {
        let model = makeModel()

        await model.export(.pdf)
        XCTAssertEqual(exporter.writes.last?.fileName, "mageride-wallet-all-all.pdf")

        let today = IosBusinessDateKt.colomboBusinessDateNow()
        await model.setRange(from: today, to: today)
        await model.export(.csv)
        XCTAssertEqual(exporter.writes.last?.fileName, "mageride-wallet-\(today)-\(today).csv")
    }

    func testASuccessfulDownloadRaisesTheShareSheet() async {
        let model = makeModel()

        await model.export(.csv)

        XCTAssertNotNil(model.state.exported)
        XCTAssertNil(model.state.exporting)
        model.dismissExported()
        XCTAssertNil(model.state.exported)
    }

    func testAFileThatCouldNotBeWrittenIsItsOwnCopy() async {
        exporter.url = nil
        let model = makeModel()

        await model.export(.csv)

        XCTAssertEqual(model.state.errorKey, "wallet_statement_failed")
        XCTAssertNil(model.state.exported)
    }

    func testAFailedDownloadBecomesCopyAndNothingIsWritten() async {
        wallet.nextStatementFailure = TestWalletFailure()
        let model = makeModel()

        await model.export(.csv)

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertTrue(exporter.writes.isEmpty)
        XCTAssertNil(model.state.exporting)
    }

    // MARK: -

    func testAFailedReadBecomesCopy() async {
        wallet.nextTransactionsFailure = TestWalletFailure()
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)
    }

    func testNothingIsReadWithoutASession() async {
        identity.driverId = nil
        let model = makeModel()

        await model.refresh()
        await model.export(.csv)

        XCTAssertTrue(wallet.transactionReads.isEmpty)
        XCTAssertTrue(wallet.statementReads.isEmpty)
    }
}
