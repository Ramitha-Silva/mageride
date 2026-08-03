package lk.mageride.driver.wallet

import kotlinx.coroutines.CancellationException
import lk.mageride.shared.data.api.subscription.SubscriptionApi
import lk.mageride.shared.data.api.wallet.WalletApi
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import lk.mageride.shared.domain.wallet.DailyFeeSchedule
import lk.mageride.shared.domain.wallet.WalletStanding

/**
 * Everything SCR-DA-021 prints and SCR-DA-025 lists.
 *
 * **The balance is a read of the ledger and nothing here computes one** (D-09). `billing.journal_entries`
 * plus its balanced postings are the truth, `billing.accounts.balance_minor` is the materialised
 * read model, and `GET /v1/wallet/{userId}` is a read of that. `:shared`'s [WalletStanding] holds
 * the server's figure; a screen that added up the history page would produce a second total that
 * disagrees the moment a posting lands between two reads.
 *
 * **Three reads, three services, each best-effort.** The balance is wallet-svc's, today's fee is
 * subscription-svc's, and the seven-tier rate table is subscription-svc's config. A driver whose
 * fee read failed still needs to see their money, so a dead read leaves its own field `null` — the
 * rule `StandbyRepository` already follows for the dashboard header.
 */
internal class WalletRepository(private val wallet: WalletApi, private val subscription: SubscriptionApi) {

    /** The whole of SCR-DA-021's balance block and daily-fee card, per-field best effort. */
    suspend fun standing(driverId: Ulid): WalletFeeStanding = WalletFeeStanding(
        wallet = read { wallet.getWallet(driverId) },
        dailyFee = read { subscription.getTodaysDailyFee(driverId) },
        // `GET /v1/fees/rates` is the seven-tier table (D5' §2.1). It is read rather than baked
        // because the rates are admin-configurable; `DailyFeeSchedule.D5_DEFAULTS` is the fallback
        // for a client that has not read it yet, exactly as `DriverLevelRules.D5_DEFAULTS` is.
        schedule = read { DailyFeeSchedule(subscription.listDailyFeeRates().items) },
    )

    /**
     * `GET /v1/wallet/{userId}` alone — the balance, without the fee card around it.
     *
     * SCR-DA-023 and SCR-DA-024 need the spendable figure and nothing else: what a transfer is
     * checked against is [WalletStanding.available], and reading a rate table to answer that
     * would be two calls nobody asked for.
     */
    suspend fun balance(driverId: Ulid): WalletStanding = WalletStanding.of(wallet.getWallet(driverId))

    /**
     * `GET /v1/wallet/{userId}/transactions` — the ledger for a Colombo date range, newest first.
     *
     * The dates are **Asia/Colombo business dates** (D-13, D-38), which is what wallet-svc filters
     * on; a range derived from the handset's zone is wrong for five and a half hours a day.
     */
    suspend fun transactions(driverId: Ulid, from: BusinessDate?, to: BusinessDate?): List<WalletTransaction> =
        wallet.listWalletTransactions(userId = driverId, from = from, to = to, page = PageRequest.FIRST).items

    /**
     * The same rows as a statement (US-9A.19).
     *
     * **This is the whole of "receipt download" on this platform.** wallet-svc has no
     * per-transaction receipt route — `GET …/transactions` with an `Accept` of `text/csv` or
     * `application/pdf` is the only download `wallet.yaml` declares, and a statement over the
     * range the driver is looking at is what US-9A.19 asks for. Recorded in the C073 handoff.
     */
    suspend fun statement(driverId: Ulid, format: StatementFormat, from: BusinessDate?, to: BusinessDate?): ByteArray =
        when (format) {
            StatementFormat.CSV -> wallet.downloadWalletStatementCsv(driverId, from, to).encodeToByteArray()
            StatementFormat.PDF -> wallet.downloadWalletStatementPdf(driverId, from, to)
        }

    /** Attempts [block], answering `null` for anything short of cancellation. */
    @Suppress("TooGenericExceptionCaught") // A dead fee read must not blank the balance.
    private suspend fun <T> read(block: suspend () -> T): T? = try {
        block()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }
}

/** Which media type `GET /v1/wallet/{userId}/transactions` is asked for. */
internal enum class StatementFormat(val extension: String, val mimeType: String) {
    CSV("csv", "text/csv"),
    PDF("pdf", "application/pdf"),
}

/**
 * SCR-DA-021's two blocks, read in one pass.
 *
 * @property wallet The read-only balance (US-9.7); `null` when wallet-svc did not answer.
 * @property dailyFee Today's fee row — rate, PAID/UNPAID, trips, first-trip-free (US-9.1/9.7).
 * @property schedule The seven-tier rate table, for the vehicle-specific rate the card names.
 */
internal data class WalletFeeStanding(
    val wallet: Wallet? = null,
    val dailyFee: TodaysDailyFee? = null,
    val schedule: DailyFeeSchedule? = null,
) {

    /** `:shared`'s projection of the balance, or `null` while the read has not answered. */
    val standing: WalletStanding? get() = wallet?.let(WalletStanding::of)

    /** What may actually be spent — the balance net of accrued penalty debt (D-05). */
    val available: Money? get() = standing?.available

    /**
     * The rate the fee card prints, for **this driver's vehicle**.
     *
     * The schedule wins over `TodaysDailyFee.dailyRateMinor` when it has a tier for the type: the
     * fee row is a snapshot of the day it was written and the schedule is what will be charged
     * next. They agree in every ordinary case; when they do not, the newer figure is the honest one.
     */
    val dailyRate: Money?
        get() = dailyFee?.let { fee ->
            schedule?.rateFor(fee.vehicleType) ?: Money(amountMinor = fee.dailyRateMinor)
        }

    /** Whether today's fee has already been taken (US-9.4 — trips 2..N are free after it). */
    val feePaid: Boolean get() = dailyFee?.status == DailyFeeDayStatus.PAID

    /**
     * Whether the wireframe's *"(1st trip free)"* qualifier applies right now.
     *
     * `firstTripFree` alone is not enough: it stays `true` for a driver who has taken no trips at
     * all, and *"PAID ✓ (1st trip free)"* on a day with a `PAID` row would be describing two
     * different days at once.
     */
    val firstTripStillFree: Boolean get() = dailyFee?.firstTripFree == true && !feePaid
}
