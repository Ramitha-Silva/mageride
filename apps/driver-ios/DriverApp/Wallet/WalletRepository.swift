import Foundation
import MageRideShared

/// SCR-DI-021's two blocks, read in one pass.
///
/// - Parameters:
///   - wallet: The read-only balance (US-9.7); `nil` when wallet-svc did not answer.
///   - dailyFee: Today's fee row — rate, PAID/UNPAID, trips, first-trip-free (US-9.1/9.7).
///   - schedule: The seven-tier rate table, for the vehicle-specific rate the card names.
struct WalletFeeStanding {

    var wallet: Wallet?
    var dailyFee: TodaysDailyFee?
    var schedule: DailyFeeSchedule?

    init(wallet: Wallet? = nil, dailyFee: TodaysDailyFee? = nil, schedule: DailyFeeSchedule? = nil) {
        self.wallet = wallet
        self.dailyFee = dailyFee
        self.schedule = schedule
    }

    /// `:shared`'s projection of the balance, or `nil` while the read has not answered.
    var standing: WalletStanding? {
        wallet.map { WalletStanding.companion.of(wallet: $0) }
    }

    /// What may actually be spent — the balance net of accrued penalty debt (D-05).
    var availableMinor: Int64? { standing?.available.amountMinor }

    /// The rate the fee card prints, for **this driver's vehicle**.
    ///
    /// The schedule wins over `TodaysDailyFee.dailyRateMinor` when it has a tier for the type: the fee
    /// row is a snapshot of the day it was written and the schedule is what will be charged next. They
    /// agree in every ordinary case; when they do not, the newer figure is the honest one.
    var dailyRateMinor: Int64? {
        guard let dailyFee else { return nil }
        return schedule?.rateFor(vehicleType: dailyFee.vehicleType)?.amountMinor ?? dailyFee.dailyRateMinor
    }

    /// Whether today's fee has already been taken (US-9.4 — trips 2..N are free after it).
    var isFeePaid: Bool { dailyFee?.status == DailyFeeDayStatus.paid }

    /// Whether the wireframe's *"(1st trip free)"* qualifier applies right now.
    ///
    /// `firstTripFree` alone is not enough: it stays `true` for a driver who has taken no trips at all,
    /// and *"PAID ✓ (1st trip free)"* on a day with a `PAID` row would be describing two different days
    /// at once.
    var isFirstTripStillFree: Bool { dailyFee?.firstTripFree == true && !isFeePaid }
}

/// Which media type `GET /v1/wallet/{userId}/transactions` is asked for.
enum StatementFormat: String, CaseIterable, Identifiable {

    case csv
    case pdf

    var id: String { rawValue }

    /// The file extension, and what the confirmation banner names.
    var fileExtension: String { rawValue }

    /// The UTI the share sheet types the file with.
    var contentType: String {
        switch self {
        case .csv: return "public.comma-separated-values-text"
        case .pdf: return "com.adobe.pdf"
        }
    }
}

/// Everything SCR-DI-021 prints and SCR-DI-025 lists.
///
/// **The balance is a read of the ledger and nothing here computes one** (D-09). `billing.journal_entries`
/// plus its balanced postings are the truth, `billing.accounts.balance_minor` is the materialised read
/// model, and `GET /v1/wallet/{userId}` is a read of that. `:shared`'s `WalletStanding` holds the
/// server's figure; a screen that added up a history page would produce a second total that disagrees
/// the moment a posting lands between two reads.
///
/// A protocol for the reason every seam in this target is one: `WalletApi` is a Kotlin interface and
/// cannot be stood in for from Swift, so a model test with no gateway needs the seam on this side of
/// the bridge.
protocol WalletRepository: AnyObject {

    /// The whole of SCR-DI-021's balance block and daily-fee card, per-field best effort.
    func standing(driverId: String) async -> WalletFeeStanding

    /// `GET /v1/wallet/{userId}` alone — the balance, without the fee card around it.
    func balance(driverId: String) async throws -> WalletStanding

    /// `GET /v1/wallet/{userId}/transactions` — the ledger for a Colombo date range, newest first.
    func transactions(driverId: String, from: BusinessDate?, to: BusinessDate?) async throws -> [WalletTransaction]

    /// The same rows as a statement (US-9A.19).
    func statement(
        driverId: String,
        format: StatementFormat,
        from: BusinessDate?,
        to: BusinessDate?
    ) async throws -> Data
}

/// ``WalletRepository`` over `:shared`'s typed wallet and subscription clients.
///
/// **Three reads, three services, each best-effort.** The balance is wallet-svc's, today's fee is
/// subscription-svc's, and the seven-tier rate table is subscription-svc's config. A driver whose fee
/// read failed still needs to see their money, so a dead read leaves its own field `nil` — the rule
/// ``ApiStandbyRepository`` already follows for the dashboard header.
final class ApiWalletRepository: WalletRepository {

    private let wallet: WalletApi
    private let subscription: SubscriptionApi

    init(wallet: WalletApi, subscription: SubscriptionApi) {
        self.wallet = wallet
        self.subscription = subscription
    }

    func standing(driverId: String) async -> WalletFeeStanding {
        WalletFeeStanding(
            wallet: await read { try await self.wallet.getWallet(userId: driverId) },
            dailyFee: await read { try await self.subscription.getTodaysDailyFee(driverId: driverId) },
            // `GET /v1/fees/rates` is the seven-tier table (D5' §2.1). It is read rather than baked
            // because the rates are admin-configurable; `DailyFeeSchedule.D5_DEFAULTS` is the fallback
            // for a client that has not read it yet, exactly as `DriverLevelRules.D5_DEFAULTS` is.
            schedule: await read { try await self.readSchedule() }
        )
    }

    /// SCR-DI-023 and SCR-DI-024 need the spendable figure and nothing else: what a transfer is checked
    /// against is `WalletStanding.available`, and reading a rate table to answer that would be two calls
    /// nobody asked for.
    func balance(driverId: String) async throws -> WalletStanding {
        WalletStanding.companion.of(wallet: try await wallet.getWallet(userId: driverId))
    }

    /// The dates are **Asia/Colombo business dates** (D-13, D-38), which is what wallet-svc filters on;
    /// a range derived from the handset's zone is wrong for five and a half hours a day.
    func transactions(driverId: String, from: BusinessDate?, to: BusinessDate?) async throws -> [WalletTransaction] {
        try await wallet.listWalletTransactions(
            userId: driverId,
            from: from,
            to: to,
            page: PageRequest.companion.FIRST
        ).items
    }

    /// **This is the whole of "receipt download" on this platform.** wallet-svc has no per-transaction
    /// receipt route — `GET …/transactions` with an `Accept` of `text/csv` or `application/pdf` is the
    /// only download `wallet.yaml` declares, and a statement over the range the driver is looking at is
    /// what US-9A.19 asks for. Recorded in the C073 handoff and carried forward.
    func statement(
        driverId: String,
        format: StatementFormat,
        from: BusinessDate?,
        to: BusinessDate?
    ) async throws -> Data {
        switch format {
        case .csv:
            let csv = try await wallet.downloadWalletStatementCsv(userId: driverId, from: from, to: to)
            return Data(csv.utf8)
        case .pdf:
            // Through `:shared`'s own converter, never `KotlinByteArray.get(index:)` in a loop —
            // that is one Objective-C message per byte and a statement is hundreds of kilobytes.
            // See `IosBytes.kt`, which is `IosCapturedDocument.kt`'s mirror.
            let bytes = try await wallet.downloadWalletStatementPdf(userId: driverId, from: from, to: to)
            return IosBytesKt.nsDataOf(bytes: bytes) as Data
        }
    }

    /// `GET /v1/fees/rates`, validated before it is handed to Kotlin.
    ///
    /// **`DailyFeeSchedule`'s constructor carries a `require`, and on this platform that is a crash
    /// rather than an exception** — a Kotlin `require` inside a non-suspend, non-`@Throws` function
    /// reaches Swift as an uncaught Objective-C exception, which Swift cannot catch and which
    /// terminates the process. The Android twin's `launchGuarded` catches the same failure and shows
    /// the driver an error. So the one thing that constructor refuses — two rates for one vehicle type —
    /// is checked here first and answered as an ordinary Swift `throw`, which ``standing(driverId:)``'s
    /// best-effort read then folds into a `nil` schedule exactly as a network failure would.
    ///
    /// The same hazard applies to ``ApiTopUpRepository/voucherTiers()``; see its own note.
    private func readSchedule() async throws -> DailyFeeSchedule {
        let rates = try await subscription.listDailyFeeRates().items
        let types = Set(rates.map(\.vehicleType))
        guard types.count == rates.count else { throw WalletContractViolation.duplicateFeeRate }
        return DailyFeeSchedule(rates: rates)
    }

    /// Attempts `block`, answering `nil` for anything that throws.
    ///
    /// A dead fee read must not blank the balance. Nothing is logged or surfaced: a rate that is absent
    /// for a minute is a rate that is absent, and a wallet that refused to draw because
    /// subscription-svc was down would be a driver who cannot see their money.
    private func read<T>(_ block: () async throws -> T) async -> T? {
        try? await block()
    }
}

/// A response the contract permits and `:shared`'s value types refuse.
///
/// Raised in Swift so the refusal is a caught failure with copy behind it rather than the process
/// termination a Kotlin `require` produces on this platform. Both cases below are server-side data
/// faults a client can only report; neither is anything a driver did.
enum WalletContractViolation: Error {

    /// `GET /v1/fees/rates` answered two rates for one vehicle type (`DailyFeeSchedule`'s own rule).
    case duplicateFeeRate

    /// `GET /v1/wallet/voucher/discount-tiers` answered a tier `VoucherCatalogue` refuses — a repeated
    /// denomination, a non-positive one, or a discount outside 0–100%.
    case malformedVoucherTier
}
