package lk.mageride.shared.domain.wallet

import kotlinx.datetime.LocalDate
import kotlinx.datetime.TimeZone
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.domain.fare.colombo
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C016 definition of done: **the daily-fee model treats the first trip of an Asia/Colombo day
 * as free and charges before the second** (D-13, US-9.1/9.4, D5' §2).
 */
class DailyFeeTest {

    private val schedule = DailyFeeSchedule.D5_DEFAULTS
    private val driver = "01JDRV0000000000000000001"
    private val vehicle = "01JVEH0000000000000000001"

    // ----------------------------------------------------------------------------------------
    // §2.2's charge logic
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_first_trip_of_the_day_is_free_and_the_wallet_is_not_even_looked_at() {
        // US-9.1: "First trip of the calendar day (Asia/Colombo) is FREE — no wallet check." A
        // driver with an empty wallet still gets their first trip; that is what makes the rule
        // "first trip free" rather than "first trip free if you can afford the second".
        val decision = DailyFeeRules.decide(
            rate = schedule.rateFor(VehicleType.SEDAN),
            tripsToday = 0,
            alreadyChargedToday = false,
            available = Money.ZERO,
        )

        assertEquals(DailyFeeDecision.WaivedFirstTrip, decision)
        assertTrue(decision.allowsTrip)
    }

    @Test
    fun the_fee_is_charged_before_the_second_trip() {
        val decision = DailyFeeRules.decide(
            rate = schedule.rateFor(VehicleType.SEDAN),
            tripsToday = 1,
            alreadyChargedToday = false,
            available = Money.ofMinor(50_000),
        )

        assertEquals(DailyFeeDecision.Charge(Money.ofMinor(20_000)), decision)
        assertTrue(decision.allowsTrip)
    }

    @Test
    fun trips_three_onwards_are_free_because_the_day_is_already_paid() {
        // US-9.4: "Single flat charge regardless of trip count." The D-13 primary key
        // (driver_id, vehicle_id, fee_date) is what makes accepting trips 2..N idempotent.
        (2..10).forEach { tripsToday ->
            val decision = DailyFeeRules.decide(
                rate = schedule.rateFor(VehicleType.SEDAN),
                tripsToday = tripsToday,
                alreadyChargedToday = true,
                available = Money.ZERO,
            )
            assertEquals(DailyFeeDecision.AlreadyChargedToday, decision, "trip ${tripsToday + 1}")
            assertTrue(decision.allowsTrip)
        }
    }

    @Test
    fun an_insufficient_balance_refuses_the_second_trip_and_names_the_shortfall() {
        val decision = DailyFeeRules.decide(
            rate = schedule.rateFor(VehicleType.VAN),
            tripsToday = 1,
            alreadyChargedToday = false,
            available = Money.ofMinor(12_000),
        )

        val refused = assertIs<DailyFeeDecision.InsufficientBalance>(decision)
        assertEquals(Money.ofMinor(30_000), refused.required)
        assertEquals(Money.ofMinor(18_000), refused.shortfall)
        assertFalse(decision.allowsTrip, "the request is missed (US-9.1)")
    }

    @Test
    fun the_exact_fee_is_enough() {
        val decision = DailyFeeRules.decide(
            rate = Money.ofMinor(20_000),
            tripsToday = 1,
            alreadyChargedToday = false,
            available = Money.ofMinor(20_000),
        )

        assertIs<DailyFeeDecision.Charge>(decision)
    }

    @Test
    fun mode_a_is_free_and_an_unconfigured_type_is_not_charged_at_zero() {
        val busDecision = DailyFeeRules.decide(
            rate = schedule.rateFor(VehicleType.BUS),
            tripsToday = 5,
            alreadyChargedToday = false,
            available = Money.ZERO,
        )
        assertEquals(DailyFeeDecision.ModeAFree, busDecision)
        assertTrue(busDecision.allowsTrip)

        // §20 seeds no plan for truck / mini_truck (C005), so Finance must set one before a
        // delivery vehicle goes online. Charging zero would let one work for free.
        assertNull(schedule.rateFor(VehicleType.TRUCK))
        assertEquals(
            DailyFeeDecision.RateNotConfigured,
            DailyFeeRules.decide(rate = null, tripsToday = 1, alreadyChargedToday = false, available = Money.ZERO),
        )
    }

    @Test
    fun the_us_9_1_warning_fires_on_trip_one_when_trip_two_would_be_refused() {
        // "On accepting trip 1, if balance < fee for trip 2 → warning push." Running it on the
        // device is what lets the driver top up while they are still moving.
        val rate = schedule.rateFor(VehicleType.THREE_WHEELER)

        assertTrue(
            DailyFeeRules.willBlockNextTrip(rate, tripsToday = 0, alreadyChargedToday = false, Money.ofMinor(5_000)),
        )
        assertFalse(
            DailyFeeRules.willBlockNextTrip(rate, tripsToday = 0, alreadyChargedToday = false, Money.ofMinor(20_000)),
        )
        assertFalse(
            DailyFeeRules.willBlockNextTrip(rate, tripsToday = 0, alreadyChargedToday = true, Money.ZERO),
            "a day already paid for cannot be refused",
        )
    }

    // ----------------------------------------------------------------------------------------
    // D-13 / D-38 — the day is an Asia/Colombo day
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_fee_date_is_the_colombo_date_not_the_utc_one() {
        // A Colombo day starts at 18:30Z the evening before, so between 18:30Z and midnight the two
        // calendars disagree: 05:00 on 27 July in Colombo is still 26 July in UTC. A driver who took
        // their free first trip at 05:00 and a second at 07:00 would be waived twice if the fee date
        // were answered from UTC.
        val earlyMorning = colombo(5, 0, day = 27)

        assertEquals(LocalDate(2026, 7, 27), DailyFeeRules.feeDate(earlyMorning))
        assertEquals(LocalDate(2026, 7, 26), DailyFeeRules.feeDate(earlyMorning, TimeZone.UTC))
    }

    @Test
    fun midnight_in_colombo_starts_a_new_fee_date() {
        assertEquals(LocalDate(2026, 7, 27), DailyFeeRules.feeDate(colombo(23, 59, day = 27)))
        assertEquals(LocalDate(2026, 7, 28), DailyFeeRules.feeDate(colombo(0, 0, day = 28)))
    }

    // ----------------------------------------------------------------------------------------
    // The tiers and the ledger key
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_seven_tiers_are_the_ones_in_the_spec() {
        // D5' §2.1: Bus/Train free · Motorbike Rs 50 · Three-wheeler Rs 100 · Flex Rs 150 ·
        // Sedan Rs 200 · Mini Van Rs 250 · Van Rs 300.
        val expected = mapOf(
            VehicleType.BUS to 0L,
            VehicleType.TRAIN to 0L,
            VehicleType.MOTORBIKE to 5_000L,
            VehicleType.THREE_WHEELER to 10_000L,
            VehicleType.FLEX to 15_000L,
            VehicleType.SEDAN to 20_000L,
            VehicleType.MINI_VAN to 25_000L,
            VehicleType.VAN to 30_000L,
        )

        assertEquals(expected.keys, schedule.pricedTypes)
        expected.forEach { (type, minor) ->
            assertEquals(Money.ofMinor(minor), schedule.rateFor(type), type.wire)
        }
        assertEquals(7, expected.values.toSet().size, "seven distinct rates over eight rows")
    }

    @Test
    fun a_charged_fee_debits_the_driver_and_credits_the_platform() {
        val entry = DailyFeeRules.entryFor(driver, vehicle, LocalDate(2026, 7, 27), Money.ofMinor(20_000))

        assertEquals(Money.ofMinor(-20_000), entry.netFor(LedgerAccount.driver(driver)))
        assertEquals(Money.ofMinor(20_000), entry.netFor(LedgerAccount.PLATFORM))
        assertEquals(0L, entry.postings.sumOf { it.amountMinor })
    }

    @Test
    fun the_ledger_key_is_spelled_exactly_as_c005_pinned_it() {
        // `billing.daily_fee_charges` carries no `journal_entry_id`: this key is the only link
        // between the charge row and its entry, and C047 must use the same spelling.
        assertEquals(
            "daily_fee:$driver:$vehicle:2026-07-27",
            DailyFeeRules.idempotencyKey(driver, vehicle, LocalDate(2026, 7, 27)),
        )
    }
}
