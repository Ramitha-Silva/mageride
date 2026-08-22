import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes and fixtures cluster 3's money screens are driven by.
///
/// Same rule as ``DashboardTestKit`` and ``JobsTestKit``: every seam here is a **Swift protocol**,
/// because the Kotlin types behind them are interfaces Swift cannot stand in for. The DTOs underneath
/// are the real shared ones — a fixture is built with the same initialiser the gateway's response
/// deserialises into, so a contract change fails these tests rather than a driver's phone.
///
/// **Every repository is recorded, not counted.** Half of what C091's rules say is *which* call ran: a
/// voucher goes to `purchaseVoucher` and never to `topUp`, an approval re-reads the balance, and a
/// rejected request never joins the history. A counter cannot say any of that.

/// A second driver, so a self-transfer and a transfer to somebody else are different tests.
// Crockford base32 has no `O` and no `L` — it spells them `0` and `1` — so "HOLDER" is `H01DER`
// here. The old spelling failed `PlatformId.isValid`, which is the rule this id exists to satisfy.
let testHolderId = "01JH01DER00000000000000001"

let testTransferId = "01JTRANSFER0000000000000A"

/// `GET /v1/wallet/{userId}` — a wallet with money in it and no debt.
func driverWallet(
    balanceMinor: Int64 = 124_000,
    availableMinor: Int64? = nil,
    outstandingDebtMinor: Int64? = nil
) -> Wallet {
    Wallet(
        userId: testDriverId,
        balanceMinor: balanceMinor,
        availableMinor: availableMinor ?? balanceMinor,
        outstandingDebtMinor: outstandingDebtMinor.map { KotlinLong(value: $0) },
        currency: Currency.lkr,
        updatedAt: nil
    )
}

/// `GET /v1/fees/{driverId}/today`.
func todaysDailyFee(
    vehicleType: VehicleType = VehicleType.threeWheeler,
    dailyRateMinor: Int64 = 10_000,
    status: DailyFeeDayStatus = DailyFeeDayStatus.unpaid,
    tripsToday: Int32 = 0,
    firstTripFree: Bool = true
) -> TodaysDailyFee {
    TodaysDailyFee(
        vehicleType: vehicleType,
        vehicleId: "01JVEHICLE000000000000001",
        dailyRateMinor: dailyRateMinor,
        status: status,
        deductedMinor: nil,
        tripsToday: tripsToday,
        firstTripFree: firstTripFree,
        feeDate: IosBusinessDateKt.colomboBusinessDateNow(),
        feeDateTzAt: nil
    )
}

/// `GET /v1/fees/rates` — D5' §2.1's own tiers, as a schedule.
func feeSchedule(threeWheelerMinor: Int64 = 10_000) -> DailyFeeSchedule {
    DailyFeeSchedule(rates: [
        DailyFeeRate(
            vehicleType: VehicleType.threeWheeler,
            dailyFeeMinor: threeWheelerMinor,
            mode: ServiceMode.c,
            currency: Currency.lkr
        ),
    ])
}

/// One rung of the bulk-voucher ladder (`billing.voucher_discount_tiers`).
func voucherTier(denominationMinor: Int64, discountBps: Int32, active: Bool = true) -> VoucherDiscountTier {
    VoucherDiscountTier(
        denominationMinor: denominationMinor,
        discountBps: discountBps,
        active: active,
        updatedAt: nil
    )
}

/// The wireframe's own ladder — `1k +10%`, `2k +12%`, `5k +15%`.
let testVoucherTiers: [VoucherDiscountTier] = [
    voucherTier(denominationMinor: 100_000, discountBps: 1_000),
    voucherTier(denominationMinor: 200_000, discountBps: 1_200),
    voucherTier(denominationMinor: 500_000, discountBps: 1_500),
]

func topup(
    topupId: String = "01JTOPUP0000000000000001",
    state: TopupState = TopupState.pending,
    amountMinor: Int64 = 200_000,
    redirectUrl: String? = "https://checkout.onepay.lk/session/abc",
    paymentLink: String? = nil,
    qrPayload: String? = nil
) -> Topup {
    Topup(
        topupId: topupId,
        state: state,
        amountMinor: amountMinor,
        currency: Currency.lkr,
        redirectUrl: redirectUrl,
        sessionToken: nil,
        paymentLink: paymentLink,
        qrPayload: qrPayload
    )
}

/// `POST /v1/vouchers/purchase` — `creditedMinor` is always the face value (`ck_voucher_credit_full`).
func voucherPurchase(
    denominationMinor: Int64 = 100_000,
    discountBps: Int32 = 1_000,
    paidMinor: Int64 = 90_000,
    redirectUrl: String? = "https://checkout.onepay.lk/session/voucher",
    qrPayload: String? = nil
) -> VoucherPurchase {
    VoucherPurchase(
        purchaseId: "01JVOUCHER00000000000001",
        denominationMinor: denominationMinor,
        discountBpsApplied: discountBps,
        paidMinor: paidMinor,
        creditedMinor: denominationMinor,
        currency: Currency.lkr,
        redirectUrl: redirectUrl,
        qrPayload: qrPayload
    )
}

func transferRow(
    transferId: String = testTransferId,
    counterpartyDriverId: String = testHolderId,
    counterpartyName: String? = "S. Bandara",
    amountMinor: Int64 = 100_000,
    direction: TransferDirection = TransferDirection.received,
    status: TransferStatus = TransferStatus.pending
) -> TransferRow {
    TransferRow(
        transferId: transferId,
        counterpartyDriverId: counterpartyDriverId,
        counterpartyName: counterpartyName,
        amountMinor: amountMinor,
        currency: Currency.lkr,
        direction: direction,
        status: status,
        createdAt: timestamp(testNow)
    )
}

/// One ledger line. `amountMinor` is **signed** — a debit is negative (D3' §0's own exemption).
func walletTransaction(
    entryId: String = "01JENTRY00000000000000001",
    kind: String = LedgerKinds.topup,
    amountMinor: Int64 = 200_000,
    balanceAfterMinor: Int64 = 324_000,
    reference: String? = nil,
    occurredAt: Date = testNow
) -> WalletTransaction {
    WalletTransaction(
        transactionId: "tx-" + entryId,
        entryId: entryId,
        kind: kind,
        amountMinor: amountMinor,
        currency: Currency.lkr,
        balanceAfterMinor: balanceAfterMinor,
        reference: reference,
        occurredAt: timestamp(occurredAt)
    )
}

// MARK: - Seams

/// ``WalletRepository`` with no gateway.
final class FakeWalletRepository: WalletRepository {

    var standing = WalletFeeStanding(wallet: driverWallet(), dailyFee: todaysDailyFee(), schedule: feeSchedule())
    var balances: [WalletStanding] = []
    var lines: [WalletTransaction] = []
    var statementBytes = Data("date,amount\n".utf8)
    var nextBalanceFailure: Error?
    var nextTransactionsFailure: Error?
    var nextStatementFailure: Error?

    private(set) var standingReads = 0
    private(set) var balanceReads = 0
    private(set) var transactionReads: [(from: BusinessDate?, to: BusinessDate?)] = []
    private(set) var statementReads: [(format: StatementFormat, from: BusinessDate?, to: BusinessDate?)] = []

    func standing(driverId: String) async -> WalletFeeStanding {
        standingReads += 1
        return standing
    }

    /// Answers `balances` in order and then repeats the last, so a test can say *"the balance was
    /// Rs 300 and after the approval it is Rs 200"* without programming a closure.
    func balance(driverId: String) async throws -> WalletStanding {
        balanceReads += 1
        if let failure = nextBalanceFailure {
            nextBalanceFailure = nil
            throw failure
        }
        guard !balances.isEmpty else { return WalletStanding.companion.of(wallet: driverWallet()) }
        return balances[min(balanceReads - 1, balances.count - 1)]
    }

    func transactions(driverId: String, from: BusinessDate?, to: BusinessDate?) async throws -> [WalletTransaction] {
        transactionReads.append((from, to))
        if let failure = nextTransactionsFailure {
            nextTransactionsFailure = nil
            throw failure
        }
        return lines
    }

    func statement(
        driverId: String,
        format: StatementFormat,
        from: BusinessDate?,
        to: BusinessDate?
    ) async throws -> Data {
        statementReads.append((format, from, to))
        if let failure = nextStatementFailure {
            nextStatementFailure = nil
            throw failure
        }
        return statementBytes
    }
}

/// ``CreditTransferRepository`` with no gateway.
final class FakeCreditTransferRepository: CreditTransferRepository {

    var pendingRows: [TransferRow] = []
    var historyRows: [TransferRow] = []
    var nextRequestFailure: Error?
    var nextSendFailure: Error?
    var nextPendingFailure: Error?
    var nextDecisionFailure: Error?

    /// What the decision answers with. Defaults to the row it was given, approved.
    var decisionResult: TransferRow?

    private(set) var requests: [(holderDriverId: String, amountMinor: Int64)] = []
    private(set) var sends: [(recipientDriverId: String, amountMinor: Int64)] = []
    private(set) var historyReads: [TransferDirectionFilter?] = []
    private(set) var approvals: [String] = []
    private(set) var rejections: [String] = []

    func request(holderDriverId: String, amountMinor: Int64) async throws -> TransferRow {
        requests.append((holderDriverId, amountMinor))
        try throwIf(&nextRequestFailure)
        return transferRow(counterpartyDriverId: holderDriverId, amountMinor: amountMinor)
    }

    func send(recipientDriverId: String, amountMinor: Int64) async throws -> TransferRow {
        sends.append((recipientDriverId, amountMinor))
        try throwIf(&nextSendFailure)
        return transferRow(
            counterpartyDriverId: recipientDriverId,
            amountMinor: amountMinor,
            direction: TransferDirection.sent,
            status: TransferStatus.approved
        )
    }

    func pending() async throws -> [TransferRow] {
        try throwIf(&nextPendingFailure)
        return pendingRows
    }

    func history(driverId: String, direction: TransferDirectionFilter?) async throws -> [TransferRow] {
        historyReads.append(direction)
        return historyRows
    }

    func approve(transferId: String) async throws -> TransferRow {
        approvals.append(transferId)
        try throwIf(&nextDecisionFailure)
        return decisionResult ?? transferRow(transferId: transferId, status: TransferStatus.approved)
    }

    func reject(transferId: String) async throws -> TransferRow {
        rejections.append(transferId)
        try throwIf(&nextDecisionFailure)
        return decisionResult ?? transferRow(transferId: transferId, status: TransferStatus.rejected)
    }

    private func throwIf(_ failure: inout Error?) throws {
        guard let programmed = failure else { return }
        failure = nil
        throw programmed
    }
}

/// ``TopUpRepository`` with no gateway.
final class FakeTopUpRepository: TopUpRepository {

    var tiers: [VoucherDiscountTier] = testVoucherTiers
    var startedTopup = topup()
    /// The sessions `topUpState` answers, in order; the last one repeats.
    var polledStates: [Topup] = []
    var purchase = voucherPurchase()
    var nextTiersFailure: Error?
    var nextTopUpFailure: Error?
    var nextPurchaseFailure: Error?

    private(set) var tierReads = 0
    private(set) var topUps: [(method: TopupMethod, amountMinor: Int64)] = []
    private(set) var purchases: [(denominationMinor: Int64, method: TopupMethod)] = []
    private(set) var pollReads = 0

    func voucherTiers() async throws -> VoucherCatalogue {
        tierReads += 1
        try throwIf(&nextTiersFailure)
        return VoucherCatalogue(tiers: tiers)
    }

    func topUp(method: TopupMethod, amountMinor: Int64) async throws -> Topup {
        topUps.append((method, amountMinor))
        try throwIf(&nextTopUpFailure)
        return startedTopup
    }

    func topUpState(topupId: String) async throws -> Topup {
        pollReads += 1
        guard !polledStates.isEmpty else { return topup(state: TopupState.succeeded) }
        return polledStates[min(pollReads - 1, polledStates.count - 1)]
    }

    func buyVoucher(denominationMinor: Int64, method: TopupMethod) async throws -> VoucherPurchase {
        purchases.append((denominationMinor, method))
        try throwIf(&nextPurchaseFailure)
        return purchase
    }

    private func throwIf(_ failure: inout Error?) throws {
        guard let programmed = failure else { return }
        failure = nil
        throw programmed
    }
}

/// ``PaymentHandoff`` with no `UIApplication`.
@MainActor
final class FakePaymentHandoff: PaymentHandoff {

    /// Whether a bank app claims the link. `false` is AL-15's fallback condition.
    var opens = true

    private(set) var openedUrls: [String] = []

    func openBankApp(_ url: String) async -> Bool {
        openedUrls.append(url)
        return opens
    }
}

/// ``WalletPreferences`` in memory.
final class FakeWalletPreferences: WalletPreferences {

    var lowBalanceThresholdMinor: Int64?
}

/// ``StatementExporter`` with no filesystem.
final class FakeStatementExporter: StatementExporter {

    /// `nil` is the "could not be written" branch.
    var url: URL? = URL(fileURLWithPath: "/tmp/mageride-wallet-all-all.csv")

    private(set) var writes: [(fileName: String, bytes: Data)] = []

    func write(fileName: String, bytes: Data) -> URL? {
        writes.append((fileName, bytes))
        return url
    }
}

/// A failure that is not a `MageRideError`, so it resolves to the shell's generic copy.
struct TestWalletFailure: Error {}
