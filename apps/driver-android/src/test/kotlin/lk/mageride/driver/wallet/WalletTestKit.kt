package lk.mageride.driver.wallet

import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.subscription.VoucherDiscountTier
import lk.mageride.shared.data.models.wallet.TransferDirection
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.util.BusinessCalendar
import kotlin.time.ExperimentalTime

/** The driver these tests are signed in as — `FakeApiBackend` routes by operation id, not by path. */
internal const val WALLET_DRIVER_ID: Ulid = Fixtures.DRIVER_ID

/** The other driver in a transfer. A well-formed platform id, because `WalletInput` checks. */
internal const val OTHER_DRIVER_ID: Ulid = Fixtures.RECIPIENT_ID

/** Rs 1,000 in minor units — the denomination US-9.19 gives its one worked rate for. */
internal const val ONE_THOUSAND: Long = 100_000L

/**
 * A wallet holding [balanceMinor], with [debtMinor] of it already owed.
 *
 * The two are separate because every affordability question in this cluster asks about the
 * **spendable** figure (D-05): a driver holding Rs 300 who owes Rs 200 can spend Rs 100.
 */
internal fun wallet(balanceMinor: Long, debtMinor: Long = 0L): Wallet = Wallet(
    userId = WALLET_DRIVER_ID,
    balanceMinor = balanceMinor,
    availableMinor = balanceMinor - debtMinor,
    outstandingDebtMinor = debtMinor,
)

/** Today's fee row for a three-wheeler at D5' §2.1's Rs 100 tier. */
@OptIn(ExperimentalTime::class)
internal fun todaysFee(
    paid: Boolean = false,
    tripsToday: Int = 0,
    rateMinor: Long = 10_000L,
    vehicleType: VehicleType = VehicleType.THREE_WHEELER,
): TodaysDailyFee = TodaysDailyFee(
    vehicleType = vehicleType,
    vehicleId = Fixtures.VEHICLE_ID,
    dailyRateMinor = rateMinor,
    status = if (paid) DailyFeeDayStatus.PAID else DailyFeeDayStatus.UNPAID,
    deductedMinor = if (paid) rateMinor else 0L,
    tripsToday = tripsToday,
    // The free first trip is spent by the charge, so "paid" and "still free" never coexist.
    firstTripFree = !paid,
    feeDate = BusinessCalendar.businessDate(Fixtures.NOW),
)

/** One voucher tier. `1000` bps is the 10% US-9.19 works through. */
internal fun tier(denominationMinor: Long, discountBps: Int, active: Boolean = true): VoucherDiscountTier =
    VoucherDiscountTier(denominationMinor = denominationMinor, discountBps = discountBps, active = active)

/**
 * One credit-transfer row, from the reading driver's point of view.
 *
 * The counterparty and the timestamp are fixed: no rule in this cluster reads either, and what a
 * transfer test is about is the amount, the direction and the status.
 */
@OptIn(ExperimentalTime::class)
internal fun transfer(
    transferId: Ulid,
    amountMinor: Long,
    direction: TransferDirection = TransferDirection.RECEIVED,
    status: TransferStatus = TransferStatus.PENDING,
): TransferRow = TransferRow(
    transferId = transferId,
    counterpartyDriverId = OTHER_DRIVER_ID,
    counterpartyName = "S. Bandara",
    amountMinor = amountMinor,
    direction = direction,
    status = status,
    createdAt = Fixtures.NOW,
)

/** One ledger line. [amountMinor] is **signed** — a debit is negative (D3' §0). */
@OptIn(ExperimentalTime::class)
internal fun ledgerLine(
    entryId: Ulid,
    kind: String,
    amountMinor: Long,
    balanceAfterMinor: Long = 0L,
    at: Timestamp = Fixtures.NOW,
): WalletTransaction = WalletTransaction(
    transactionId = entryId,
    entryId = entryId,
    kind = kind,
    amountMinor = amountMinor,
    balanceAfterMinor = balanceAfterMinor,
    occurredAt = at,
)

/** A one-page answer for any of the paged wallet reads. */
internal fun <T> onePage(vararg rows: T): Page<T> = Page(items = rows.toList())

/** [WalletPreferences] in memory — the production one is `SharedPreferences` (US-9.9's local line). */
internal class FakeWalletPreferences(override var lowBalanceThresholdMinor: Long? = null) : WalletPreferences

/**
 * [PaymentHandoff] that records what it was asked to open.
 *
 * [handled] is what makes AL-15's fallback testable: the rule is *"try the deep link, show the code
 * when nothing opened it"*, and the only way to reach the second half is a handset that refuses.
 */
internal class FakePaymentHandoff(var handled: Boolean = true) : PaymentHandoff {

    /** Every URL offered, oldest first. */
    val opened: MutableList<String> = mutableListOf()

    override fun open(url: String): Boolean {
        opened += url
        return handled
    }
}

/** [StatementExporter] that keeps the bytes instead of writing them. */
internal class FakeStatementExporter(var handled: Boolean = true) : StatementExporter {

    /** What the last successful export was asked to share. */
    var lastFileName: String? = null
    var lastFormat: StatementFormat? = null
    var lastBytes: ByteArray? = null

    override fun share(fileName: String, format: StatementFormat, bytes: ByteArray): Boolean {
        lastFileName = fileName
        lastFormat = format
        lastBytes = bytes
        return handled
    }
}
