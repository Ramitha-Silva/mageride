package lk.mageride.shared.domain.wallet

import kotlinx.datetime.TimeZone
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.subscription.DailyFeeDayStatus
import lk.mageride.shared.data.models.subscription.DailyFeeRate
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.util.BusinessCalendar

// The daily platform fee (D-13, D-08, URD Epic 9, D5' §2).
//
//   on driver accepts trip N for (driverId, vehicleId):
//     feeDate = today(Asia/Colombo)
//     if tripsToday == 0:  WAIVED_FIRST_TRIP, amount 0, ALLOW      # no wallet check (US-9.1)
//     else if already_charged(driverId, vehicleId, feeDate): ALLOW  # idempotent, once per day
//     else if walletBalance >= fee: post the ledger entry, PAID, ALLOW
//     else: REFUSE → "request missed: insufficient balance"
//
// SUBSCRIPTION-SVC DECIDES; THIS IS THE MIRROR. The authoritative call is
// `POST /v1/internal/fees/{driverId}/charge-before-trip`, keyed on the
// `(driver_id, vehicle_id, fee_date)` primary key so accepting trips 2..N never double-charges
// (D-13). A driver app runs the same rule locally for one reason: to warn a driver on trip 1 that
// their balance will not cover trip 2 (US-9.1), which is the whole point of the free first trip.
//
// THE DAY IS AN ASIA/COLOMBO DAY (D-13, D-38). Not the handset's day and not UTC's: at 19:00 UTC
// it is already tomorrow in Colombo, and a client answering "first trip of the day" from a device
// clock would waive a fee that had already been charged.
//
// DELIVERIES AND PASSENGER RIDES COUNT TOGETHER (P-06, §11): one flat fee per driver per vehicle
// per day, whatever mix of work it was.

/** What the daily-fee gate would do for the trip about to be accepted. */
public sealed interface DailyFeeDecision {

    /** Whether the trip may be accepted. Only [InsufficientBalance] says no. */
    public val allowsTrip: Boolean get() = this !is InsufficientBalance

    /**
     * The first trip of the Colombo day: **free, and no wallet check at all** (US-9.1, D-13).
     *
     * Writes a `WAIVED_FIRST_TRIP` row carrying zero, so "no row" keeps meaning "not charged yet
     * today" (`ck_daily_fee_charges_waiver`, C005).
     */
    public data object WaivedFirstTrip : DailyFeeDecision

    /** Today's fee is already paid. Trips 2..N are free after the first deduction (US-9.4). */
    public data object AlreadyChargedToday : DailyFeeDecision

    /** Mode A: bus and train carry no platform fee at all (D5' §2.1). */
    public data object ModeAFree : DailyFeeDecision

    /**
     * The fee is due now, before this trip.
     *
     * @property amount The tier's rate.
     */
    public data class Charge(val amount: Money) : DailyFeeDecision

    /**
     * Not enough in the wallet — the request is missed and a low-balance push goes out (US-9.1).
     *
     * Surfaces to the driver as `402 insufficient-wallet` on the accept, which C015's
     * `OfferOutcome.WalletBlocked` already keeps distinct from a dispatch failure.
     *
     * @property required The tier's rate.
     * @property available What the wallet can actually spend.
     */
    public data class InsufficientBalance(val required: Money, val available: Money) : DailyFeeDecision {

        /** How much has to be topped up before the next trip. */
        public val shortfall: Money get() = required - available
    }

    /**
     * No rate is configured for this vehicle type.
     *
     * Real, not hypothetical: §20 seeds no plan for `truck` or `mini_truck`, so Finance must set
     * one before a delivery vehicle can go online (C005). Treating a missing rate as zero would
     * let one work for free.
     */
    public data object RateNotConfigured : DailyFeeDecision
}

/**
 * The seven-tier fee schedule (`billing.plans`, D5' §2.1, admin-configurable).
 *
 * @param rates What `GET /v1/fees/rates` returned.
 */
public class DailyFeeSchedule(rates: List<DailyFeeRate>) {

    /** The tiers, in the order given. */
    public val rates: List<DailyFeeRate> = rates.toList()

    private val byType: Map<VehicleType, Money> =
        rates.associate { it.vehicleType to Money(amountMinor = it.dailyFeeMinor, currency = it.currency) }

    init {
        require(byType.size == rates.size) { "a fee schedule holds one rate per vehicle type" }
    }

    /** The rate for [vehicleType], or `null` when none is configured. */
    public fun rateFor(vehicleType: VehicleType): Money? = byType[vehicleType]

    /** The types this schedule can charge. */
    public val pricedTypes: Set<VehicleType> get() = byType.keys

    public companion object {

        /**
         * D5' §2.1's own tiers, for a client that has not read `GET /v1/fees/rates` yet.
         *
         * Bus/Train free (Mode A) · Motorbike Rs 50 · Three-wheeler Rs 100 · Flex Rs 150 ·
         * Sedan Rs 200 · Mini Van Rs 250 · Van Rs 300. Seven rates over eight rows, identical to
         * `1901__seed_fares_billing.sql`.
         *
         * `truck` and `mini_truck` are **absent**, as they are in the seed — see
         * [DailyFeeDecision.RateNotConfigured].
         */
        public val D5_DEFAULTS: DailyFeeSchedule = DailyFeeSchedule(
            listOf(
                DailyFeeRate(VehicleType.BUS, dailyFeeMinor = 0, mode = ServiceMode.A),
                DailyFeeRate(VehicleType.TRAIN, dailyFeeMinor = 0, mode = ServiceMode.A),
                DailyFeeRate(VehicleType.MOTORBIKE, dailyFeeMinor = 5_000, mode = ServiceMode.C),
                DailyFeeRate(VehicleType.THREE_WHEELER, dailyFeeMinor = 10_000, mode = ServiceMode.C),
                DailyFeeRate(VehicleType.FLEX, dailyFeeMinor = 15_000, mode = ServiceMode.C),
                DailyFeeRate(VehicleType.SEDAN, dailyFeeMinor = 20_000, mode = ServiceMode.C),
                DailyFeeRate(VehicleType.MINI_VAN, dailyFeeMinor = 25_000, mode = ServiceMode.C),
                DailyFeeRate(VehicleType.VAN, dailyFeeMinor = 30_000, mode = ServiceMode.C),
            ),
        )
    }
}

/**
 * D5' §2.2's charge logic, client side.
 */
public object DailyFeeRules {

    /** The `billing.journal_entries.kind` a fee posts under (C005 CHECK). */
    public const val JOURNAL_KIND: String = "daily_fee"

    /** Trips that are free each Colombo day. Exactly one — the first (US-9.1). */
    public const val FREE_TRIPS_PER_DAY: Int = 1

    /** The Colombo business date [at] falls in — the `fee_date` (D-13, D-38). */
    public fun feeDate(at: Timestamp, zone: TimeZone = BusinessCalendar.ZONE): BusinessDate =
        BusinessCalendar.businessDate(at, zone)

    /**
     * What the gate would decide for the trip about to be accepted.
     *
     * The order of the checks is §2.2's own and it matters: **the free first trip is decided
     * before the wallet is looked at at all**. A driver with an empty wallet still gets their first
     * trip of the day, which is what makes the rule "first trip free" rather than "first trip free
     * if you can afford the second".
     *
     * @param rate The tier's rate, or `null` when none is configured for the vehicle.
     * @param tripsToday Trips already accepted or completed this Colombo day, on any vehicle
     *   (P-06 counts deliveries with passenger rides).
     * @param alreadyChargedToday Whether a `PAID` row already exists for
     *   `(driverId, vehicleId, feeDate)` — the D-13 idempotency key.
     * @param available The wallet's spendable balance ([WalletStanding.available]).
     */
    public fun decide(
        rate: Money?,
        tripsToday: Int,
        alreadyChargedToday: Boolean,
        available: Money,
    ): DailyFeeDecision = when {
        tripsToday < FREE_TRIPS_PER_DAY -> DailyFeeDecision.WaivedFirstTrip
        alreadyChargedToday -> DailyFeeDecision.AlreadyChargedToday
        rate == null -> DailyFeeDecision.RateNotConfigured
        rate == Money.ZERO -> DailyFeeDecision.ModeAFree
        available >= rate -> DailyFeeDecision.Charge(rate)
        else -> DailyFeeDecision.InsufficientBalance(required = rate, available = available)
    }

    /** [decide] against the `GET /v1/fees/{driverId}/today` read a driver dashboard already holds. */
    public fun decide(today: TodaysDailyFee, standing: WalletStanding, schedule: DailyFeeSchedule): DailyFeeDecision =
        decide(
            rate = schedule.rateFor(today.vehicleType) ?: Money.ofMinor(today.dailyRateMinor),
            tripsToday = today.tripsToday,
            alreadyChargedToday = today.status == DailyFeeDayStatus.PAID,
            available = standing.available,
        )

    /**
     * Whether accepting the **next** trip after this one would be refused for want of balance.
     *
     * This is US-9.1's warning: "on accepting trip 1, if balance < fee for trip 2 → warning push".
     * The point of running it on the device is that the driver is told while they are still moving,
     * not when a request they wanted is missed.
     */
    public fun willBlockNextTrip(
        rate: Money?,
        tripsToday: Int,
        alreadyChargedToday: Boolean,
        available: Money,
    ): Boolean = decide(
        rate = rate,
        tripsToday = tripsToday + 1,
        alreadyChargedToday = alreadyChargedToday,
        available = available,
    ) is DailyFeeDecision.InsufficientBalance

    /**
     * The balanced entry a charged fee posts (D-09): debit the driver, credit the platform.
     *
     * **The idempotency key spelling is fixed by C005** and is repeated in a column comment there:
     * `'daily_fee:' || driver_id || ':' || vehicle_id || ':' || fee_date`. `billing.daily_fee_charges`
     * deliberately carries no `journal_entry_id`, so this key is the only link between the charge
     * row and its ledger entry — and it is what makes a redelivered charge a no-op instead of a
     * second deduction (D-13). C047 must use exactly this spelling.
     */
    public fun entryFor(driverId: Ulid, vehicleId: Ulid, feeDate: BusinessDate, amount: Money): LedgerEntry =
        LedgerEntry(
            kind = JOURNAL_KIND,
            idempotencyKey = idempotencyKey(driverId, vehicleId, feeDate),
            postings = listOf(
                LedgerPosting(LedgerAccount.driver(driverId), -amount.amountMinor),
                LedgerPosting(LedgerAccount.PLATFORM, amount.amountMinor),
            ),
        )

    /** The D-13 money key, spelled exactly as `billing.daily_fee_charges` documents it (C005). */
    public fun idempotencyKey(driverId: Ulid, vehicleId: Ulid, feeDate: BusinessDate): String =
        "$JOURNAL_KIND:$driverId:$vehicleId:$feeDate"
}
