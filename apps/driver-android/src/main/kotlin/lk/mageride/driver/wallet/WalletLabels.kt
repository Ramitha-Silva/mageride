package lk.mageride.driver.wallet

import androidx.annotation.StringRes
import lk.mageride.driver.R
import lk.mageride.shared.data.models.wallet.TransferDirection
import lk.mageride.shared.data.models.wallet.TransferStatus
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.domain.wallet.TopupMethod

/**
 * What a ledger line is called, and which of SCR-DA-025's four chips it belongs under.
 *
 * **`WalletTransaction.kind` is a machine key, not an enum** (C012 says so at the field): it
 * projects `billing.journal_entries.kind`, whose CHECK constraint grows without a contract change —
 * migration 1108 added `fleet_invoice` and 1109 added `driver_payout` after the DTO was written.
 * So the table below is a *lookup with a fallback*, never a `when` that must be exhaustive, and a
 * kind this build has not heard of renders as a plain wallet entry with its amount and date rather
 * than as a crash or as an English machine key on a Sinhala screen.
 *
 * The twelve kinds the constraint admits today are `topup`, `daily_fee`, `trip_payment`,
 * `penalty_settle`, `adjustment`, `tip_payout`, `payment_refund`, `overpaid_reversal`,
 * `voucher_purchase`, `driver_transfer`, `fleet_invoice` and `driver_payout`. A driver's own
 * account never carries `fleet_invoice` — that one posts against an organisation — so it has no
 * label of its own and falls through.
 */
internal object LedgerKinds {

    /** `billing.journal_entries.kind` values, spelled exactly as the CHECK constraint does. */
    const val TOPUP: String = "topup"
    const val DAILY_FEE: String = "daily_fee"
    const val TRIP_PAYMENT: String = "trip_payment"
    const val PENALTY_SETTLE: String = "penalty_settle"
    const val ADJUSTMENT: String = "adjustment"
    const val TIP_PAYOUT: String = "tip_payout"
    const val PAYMENT_REFUND: String = "payment_refund"
    const val OVERPAID_REVERSAL: String = "overpaid_reversal"
    const val VOUCHER_PURCHASE: String = "voucher_purchase"
    const val DRIVER_TRANSFER: String = "driver_transfer"
    const val DRIVER_PAYOUT: String = "driver_payout"

    private val LABELS: Map<String, Int> = mapOf(
        TOPUP to R.string.wallet_kind_topup,
        DAILY_FEE to R.string.wallet_kind_daily_fee,
        TRIP_PAYMENT to R.string.wallet_kind_trip_payment,
        PENALTY_SETTLE to R.string.wallet_kind_penalty,
        ADJUSTMENT to R.string.wallet_kind_adjustment,
        TIP_PAYOUT to R.string.wallet_kind_tip,
        PAYMENT_REFUND to R.string.wallet_kind_refund,
        OVERPAID_REVERSAL to R.string.wallet_kind_reversal,
        VOUCHER_PURCHASE to R.string.wallet_kind_voucher,
        DRIVER_TRANSFER to R.string.wallet_kind_transfer,
        DRIVER_PAYOUT to R.string.wallet_kind_payout,
    )

    /** Trilingual copy for [kind], or the generic line for one this build does not know. */
    @StringRes
    fun labelFor(kind: String): Int = LABELS[kind] ?: R.string.wallet_kind_other
}

/**
 * SCR-DA-025's `All · Fees · Top-ups · Transfers` chips.
 *
 * The filter runs **on the device, over the page already read**, because
 * `GET /v1/wallet/{userId}/transactions` takes a date range and no `kind` parameter — adding one
 * would be a contract change, and four chips over one page is what the wireframe draws anyway.
 * The date range is the server-side filter and it is the one the statement download uses too.
 *
 * @property label The chip's copy.
 * @property kinds The ledger kinds it keeps; empty means everything.
 */
internal enum class HistoryFilter(@param:StringRes val label: Int, val kinds: Set<String>) {

    ALL(R.string.wallet_filter_all, emptySet()),

    /** The daily platform fee, and nothing else (US-9.22, D5' §2). */
    FEES(R.string.wallet_filter_fees, setOf(LedgerKinds.DAILY_FEE)),

    /**
     * Money the driver put in.
     *
     * A bulk voucher is here rather than under its own chip: US-9.19 credits the buyer's own
     * wallet at purchase, so from the ledger's side it is a top-up that cost less than it credited.
     */
    TOPUPS(R.string.wallet_filter_topups, setOf(LedgerKinds.TOPUP, LedgerKinds.VOUCHER_PURCHASE)),

    /** Driver-to-driver credit, both directions (US-9.20/9.21). */
    TRANSFERS(R.string.wallet_filter_transfers, setOf(LedgerKinds.DRIVER_TRANSFER)),
    ;

    /** Whether [line] survives this chip. */
    fun keeps(line: WalletTransaction): Boolean = kinds.isEmpty() || line.kind in kinds
}

/**
 * The three top-up tiles (AL-05).
 *
 * `ONEPAY_CARD` and `ONEPAY_WALLET` are one endpoint and two tiles: the card-versus-wallet choice
 * is made on OnePay's own hosted page, and a tile still has to say which one it is.
 */
@StringRes
internal fun TopupMethod.labelRes(): Int = when (this) {
    TopupMethod.ONEPAY_CARD -> R.string.wallet_method_card
    TopupMethod.ONEPAY_WALLET -> R.string.wallet_method_onepay
    TopupMethod.LANKAQR -> R.string.wallet_method_lankaqr
}

/** Which way a transfer row went, from the reading driver's point of view. */
@StringRes
internal fun TransferDirection.labelRes(): Int = when (this) {
    TransferDirection.SENT -> R.string.wallet_transfer_sent
    TransferDirection.RECEIVED -> R.string.wallet_transfer_received
}

/** Where a transfer stands (`billing.credit_transfers.status`). */
@StringRes
internal fun TransferStatus.labelRes(): Int = when (this) {
    TransferStatus.PENDING -> R.string.wallet_transfer_pending
    TransferStatus.APPROVED -> R.string.wallet_transfer_approved
    TransferStatus.REJECTED -> R.string.wallet_transfer_rejected
}
