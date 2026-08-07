import Foundation
import MageRideShared

/// What a ledger line is called, and which of SCR-DI-025's four chips it belongs under.
///
/// **`WalletTransaction.kind` is a machine key, not an enum** (C012 says so at the field): it
/// projects `billing.journal_entries.kind`, whose CHECK constraint grows without a contract change —
/// migration 1108 added `fleet_invoice` and 1109 added `driver_payout` after the DTO was written. So
/// the table below is a *lookup with a fallback*, never a `switch` that must be exhaustive, and a
/// kind this build has not heard of renders as a plain wallet entry with its amount and date rather
/// than as an English machine key on a Sinhala screen.
///
/// The twelve kinds the constraint admits today are `topup`, `daily_fee`, `trip_payment`,
/// `penalty_settle`, `adjustment`, `tip_payout`, `payment_refund`, `overpaid_reversal`,
/// `voucher_purchase`, `driver_transfer`, `fleet_invoice` and `driver_payout`. A driver's own account
/// never carries `fleet_invoice` — that one posts against an organisation — so it has no label of its
/// own and falls through.
///
/// The same table is `apps/driver-android/.../wallet/WalletLabels.kt`.
enum LedgerKinds {

    /// `billing.journal_entries.kind` values, spelled exactly as the CHECK constraint does.
    static let topup = "topup"
    static let dailyFee = "daily_fee"
    static let tripPayment = "trip_payment"
    static let penaltySettle = "penalty_settle"
    static let adjustment = "adjustment"
    static let tipPayout = "tip_payout"
    static let paymentRefund = "payment_refund"
    static let overpaidReversal = "overpaid_reversal"
    static let voucherPurchase = "voucher_purchase"
    static let driverTransfer = "driver_transfer"
    static let driverPayout = "driver_payout"

    /// Trilingual copy for `kind`, or the generic line for one this build does not know.
    static func labelKey(for kind: String) -> String { labels[kind] ?? "wallet_kind_other" }

    private static let labels: [String: String] = [
        topup: "wallet_kind_topup",
        dailyFee: "wallet_kind_daily_fee",
        tripPayment: "wallet_kind_trip_payment",
        penaltySettle: "wallet_kind_penalty",
        adjustment: "wallet_kind_adjustment",
        tipPayout: "wallet_kind_tip",
        paymentRefund: "wallet_kind_refund",
        overpaidReversal: "wallet_kind_reversal",
        voucherPurchase: "wallet_kind_voucher",
        driverTransfer: "wallet_kind_transfer",
        driverPayout: "wallet_kind_payout",
    ]
}

/// SCR-DI-025's `All · Fees · Top-ups · Transfers` chips.
///
/// The filter runs **on the device, over the page already read**, because
/// `GET /v1/wallet/{userId}/transactions` takes a date range and no `kind` parameter — adding one
/// would be a contract change, and four chips over one page is what the wireframe draws anyway. The
/// date range is the server-side filter and it is the one the statement download uses too.
enum HistoryFilter: String, CaseIterable, Identifiable {

    case all
    /// The daily platform fee, and nothing else (US-9.22, D5' §2).
    case fees
    /// Money the driver put in.
    ///
    /// A bulk voucher is here rather than under its own chip: US-9.19 credits the buyer's own wallet
    /// at purchase, so from the ledger's side it is a top-up that cost less than it credited.
    case topUps
    /// Driver-to-driver credit, both directions (US-9.20/9.21).
    case transfers

    var id: String { rawValue }

    /// The chip's copy.
    var labelKey: String {
        switch self {
        case .all: return "wallet_filter_all"
        case .fees: return "wallet_filter_fees"
        case .topUps: return "wallet_filter_topups"
        case .transfers: return "wallet_filter_transfers"
        }
    }

    /// The ledger kinds this chip keeps; empty means everything.
    var kinds: Set<String> {
        switch self {
        case .all: return []
        case .fees: return [LedgerKinds.dailyFee]
        case .topUps: return [LedgerKinds.topup, LedgerKinds.voucherPurchase]
        case .transfers: return [LedgerKinds.driverTransfer]
        }
    }

    /// Whether `line` survives this chip.
    func keeps(_ line: WalletTransaction) -> Bool {
        let kinds = self.kinds
        return kinds.isEmpty || kinds.contains(line.kind)
    }
}

/// The three ways money gets into a wallet, in the order SCR-DI-022 prints them (AL-05).
///
/// `TopupMethod.entries` is Kotlin's and does not cross the bridge as anything a Swift `for` can use,
/// so the order is written out here — the same shape ``EarningsPeriods`` uses for SCR-DI-020's three
/// tabs. It is not a second source of truth: ``WalletFenceTests`` asserts this table against the shared
/// enum's own `ordinal`s, which is also where AL-05's *"there is no fourth"* is enforced — a method
/// added to `:shared` fails a test here rather than quietly missing a segment.
enum TopupMethods {

    static let all: [TopupMethod] = [
        TopupMethod.onepayCard,
        TopupMethod.onepayWallet,
        TopupMethod.lankaqr,
    ]
}

extension TopupMethod {

    /// The three top-up tiles (AL-05).
    ///
    /// `onepayCard` and `onepayWallet` are one endpoint and two tiles: the card-versus-wallet choice
    /// is made on OnePay's own hosted page, and a tile still has to say which one it is.
    var labelKey: String {
        switch self {
        case TopupMethod.onepayCard: return "wallet_method_card"
        case TopupMethod.onepayWallet: return "wallet_method_onepay"
        // `lankaqr`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default: return "wallet_method_lankaqr"
        }
    }
}

extension TransferDirection {

    /// Which way a transfer row went, from the reading driver's point of view.
    var labelKey: String {
        switch self {
        case TransferDirection.sent: return "wallet_transfer_sent"
        default: return "wallet_transfer_received"
        }
    }
}

extension TransferStatus {

    /// Where a transfer stands (`billing.credit_transfers.status`).
    var labelKey: String {
        switch self {
        case TransferStatus.pending: return "wallet_transfer_pending"
        case TransferStatus.approved: return "wallet_transfer_approved"
        default: return "wallet_transfer_rejected"
        }
    }

    /// The pill tone SCR-DI-024's history row draws it in.
    ///
    /// A **rejected** transfer is neutral rather than an error: the holder declining a request is an
    /// ordinary answer, and drawing it in `error` would tell a driver something went wrong.
    var tone: StatusTone {
        switch self {
        case TransferStatus.approved: return .done
        case TransferStatus.pending: return .pending
        default: return .neutral
        }
    }
}

extension CreditTransferRejection {

    /// Trilingual copy for a refusal the device worked out before spending a round trip.
    var messageKey: String {
        switch self {
        case CreditTransferRejection.insufficientBalance: return "error_insufficient_wallet"
        case CreditTransferRejection.nonPositiveAmount: return "wallet_amount_required"
        default: return "wallet_self_transfer"
        }
    }
}
