package lk.mageride.shared.domain.fare

import kotlinx.datetime.LocalTime
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.fare.FareBreakdown
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C016 definition of done: **the fare rounding matches D5' §1.3 exactly for a table of
 * spec-derived cases**.
 *
 * Every expectation below is worked out from §1.1's formula and §1.1's own tariff table by hand,
 * not read off the implementation. The cases are chosen to exercise each part of the formula
 * separately: the first kilometre being inside the first-km charge, a fractional distance, each
 * surcharge on its own, the additive stack of both, and the boundaries of the windows.
 */
class FareCalculatorTest {

    private val calculator = FareCalculator()

    /**
     * One row of §1.1, computed by hand.
     *
     * @property expectedBaseMinor `firstKmMinor + round(max(0, distanceKm - 1) * perKmMinor)`.
     * @property expectedSurchargeMinor `round(base * (peakPct + nightPct) / 100)`.
     */
    private data class Case(
        val vehicleType: RideVehicleType,
        val distanceKm: Double,
        val at: Timestamp,
        val expectedBaseMinor: Long,
        val expectedSurchargeMinor: Long,
        val note: String,
    ) {
        val expectedTotalMinor: Long get() = expectedBaseMinor + expectedSurchargeMinor
    }

    private val cases = listOf(
        // ---- the first kilometre is inside the first-km charge ---------------------------------
        Case(RideVehicleType.MOTORBIKE, 0.4, OFF_PEAK, 8_000, 0, "under 1 km pays the first-km charge"),
        Case(RideVehicleType.MOTORBIKE, 1.0, OFF_PEAK, 8_000, 0, "exactly 1 km pays the same"),

        // ---- whole and fractional per-km products ---------------------------------------------
        // 8000 + 4.0 x 6000
        Case(RideVehicleType.MOTORBIKE, 5.0, OFF_PEAK, 32_000, 0, "Rs 80 + 4 km at Rs 60"),
        // 15000 + 1.5 x 12000
        Case(RideVehicleType.VAN, 2.5, OFF_PEAK, 33_000, 0, "Rs 150 + 1.5 km at Rs 120"),
        // 13000 + 6.25 x 9000
        Case(RideVehicleType.FLEX, 7.25, OFF_PEAK, 69_250, 0, "a quarter-kilometre is exact"),
        // 15000 + 11.4 x 11000 — 11.4 is not representable in binary, and the product still lands
        // on the whole minor unit the spec's arithmetic gives.
        Case(RideVehicleType.MINI_VAN, 12.4, OFF_PEAK, 140_400, 0, "a repeating binary fraction"),
        // 15000 + 9.0 x 10000
        Case(RideVehicleType.SEDAN, 10.0, OFF_PEAK, 105_000, 0, "Rs 150 + 9 km at Rs 100"),

        // ---- one surcharge at a time ------------------------------------------------------------
        // 10000 + 2.0 x 8000 = 26000; +20% = 5200
        Case(RideVehicleType.THREE_WHEELER, 3.0, MORNING_PEAK, 26_000, 5_200, "peak is +20% of the base"),
        // the same base at +15%
        Case(RideVehicleType.THREE_WHEELER, 3.0, NIGHT, 26_000, 3_900, "night is +15% of the base"),
        Case(RideVehicleType.THREE_WHEELER, 3.0, OFF_PEAK, 26_000, 0, "midday is neither"),
    )

    @Test
    fun every_spec_derived_case_prices_exactly() {
        cases.forEach { case ->
            val quote = assertNotNull(
                calculator.quote(case.vehicleType, case.distanceKm, case.at),
                "${case.vehicleType.wire} has a D5 tariff",
            )
            assertEquals(case.expectedBaseMinor, quote.baseMinor, "base — ${case.note}")
            assertEquals(case.expectedSurchargeMinor, quote.surchargeMinor, "surcharge — ${case.note}")
            assertEquals(case.expectedTotalMinor, quote.totalMinor, "total — ${case.note}")
            assertEquals(case.expectedTotalMinor, quote.total.amountMinor, "total as money — ${case.note}")
        }
    }

    @Test
    fun the_total_is_an_exact_sum_and_is_never_rounded_again() {
        // §1.3: "at each additive step is avoided — single round only where a *pct/100 product is
        // taken". If the total were rounded a second time, a base+surcharge ending in an odd minor
        // unit would move; this asserts it does not.
        val quote = assertNotNull(calculator.quote(RideVehicleType.THREE_WHEELER, 3.0, MORNING_PEAK))

        assertEquals(quote.baseMinor + quote.surchargeMinor, quote.totalMinor)
        assertEquals(31_200L, quote.totalMinor)
    }

    @Test
    fun peak_and_night_stack_additively_not_multiplicatively() {
        // The seeded windows never overlap, so this needs a configuration in which they do — which
        // is the point: the percentages are admin-configurable, and §1.1 adds them.
        val overlapping = SurchargeWindows(
            listOf(
                SurchargeWindow(SurchargeKind.PEAK, LocalTime(22, 0), LocalTime(23, 0)),
                SurchargeWindow(SurchargeKind.NIGHT, LocalTime(22, 0), LocalTime(5, 0)),
            ),
        )
        val quote = assertNotNull(
            FareCalculator(windows = overlapping).quote(RideVehicleType.SEDAN, 10.0, colombo(22, 30)),
        )

        // base 105000 at (20 + 15)% = 36750. Compounded it would be 105000 x 1.20 x 1.15 = 144900
        // — a Rs 31.50 difference on one ride, and the wrong number.
        assertEquals(35, quote.percentages.totalPct)
        assertEquals(36_750L, quote.surchargeMinor)
        assertEquals(141_750L, quote.totalMinor)
    }

    @Test
    fun a_vehicle_type_with_no_configured_rate_is_not_priced() {
        // §20 seeds no tariff for truck / mini_truck (C005) — Finance must configure one before a
        // delivery vehicle can go online. Quoting them at zero would be a free delivery.
        assertNull(calculator.quote(RideVehicleType.TRUCK, 5.0, OFF_PEAK))
        assertNull(calculator.quote(RideVehicleType.MINI_TRUCK, 5.0, OFF_PEAK))
        assertTrue(RideVehicleType.TRUCK !in TariffTable.D5_DEFAULTS.pricedTypes)
    }

    @Test
    fun the_six_passenger_tiers_all_price() {
        val passengerTypes = RideVehicleType.entries.filterNot { it.isDeliveryOnly }

        assertEquals(6, passengerTypes.size)
        passengerTypes.forEach {
            assertNotNull(calculator.quote(it, 3.0, OFF_PEAK), "${it.wire} must have a D5 tariff")
        }
    }

    @Test
    fun the_d5_tariff_table_is_the_spec_table() {
        // §1.1's table, re-declared here rather than read from the implementation.
        val expected = mapOf(
            RideVehicleType.MOTORBIKE to (8_000L to 6_000L),
            RideVehicleType.THREE_WHEELER to (10_000L to 8_000L),
            RideVehicleType.FLEX to (13_000L to 9_000L),
            RideVehicleType.SEDAN to (15_000L to 10_000L),
            RideVehicleType.MINI_VAN to (15_000L to 11_000L),
            RideVehicleType.VAN to (15_000L to 12_000L),
        )

        assertEquals(expected.keys, TariffTable.D5_DEFAULTS.pricedTypes)
        expected.forEach { (type, rates) ->
            val tariff = TariffTable.D5_DEFAULTS.requireOf(type)
            assertEquals(rates.first, tariff.firstKmMinor, "${type.wire} first km")
            assertEquals(rates.second, tariff.perKmMinor, "${type.wire} per km")
            assertEquals(20, tariff.peakSurchargePct, "${type.wire} peak")
            assertEquals(15, tariff.nightSurchargePct, "${type.wire} night")
        }
    }

    @Test
    fun a_negative_or_non_finite_distance_is_refused() {
        val tariff = TariffTable.D5_DEFAULTS.requireOf(RideVehicleType.SEDAN)

        assertFailsWith<IllegalArgumentException> { calculator.quoteWith(tariff, -1.0, OFF_PEAK) }
        assertFailsWith<IllegalArgumentException> { calculator.quoteWith(tariff, Double.NaN, OFF_PEAK) }
    }

    @Test
    fun a_tariff_table_refuses_two_rows_for_one_type() {
        // Two rows for one type means two `effective_from` versions have been flattened together;
        // silently picking one would price rides off a rate nobody chose.
        assertFailsWith<IllegalArgumentException> {
            TariffTable(
                listOf(
                    Tariff(RideVehicleType.SEDAN, firstKmMinor = 15_000, perKmMinor = 10_000),
                    Tariff(RideVehicleType.SEDAN, firstKmMinor = 16_000, perKmMinor = 10_000),
                ),
            )
        }
    }

    // ----------------------------------------------------------------------------------------
    // Rendering the server's answer — the number the passenger is actually charged
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_server_estimate_is_rendered_from_its_own_total_never_recomputed() {
        // The server's total wins even when it disagrees with the client's arithmetic: the
        // `fareEstimateToken` binds that figure, and rendering a different one would show a
        // passenger a price they are not about to be charged.
        val response = FareEstimateResponse(
            fareEstimateToken = "tok_01",
            amountMinor = 31_207,
            breakdown = FareBreakdown(
                firstKmMinor = 10_000,
                perKmMinor = 8_000,
                distanceKm = 3.0,
                peakSurchargePct = 20,
            ),
        )

        val quote = FareCalculator.of(response, RideVehicleType.THREE_WHEELER)

        assertEquals(31_207L, quote.totalMinor)
        assertEquals(26_000L, quote.baseMinor)
        assertEquals(5_207L, quote.surchargeMinor, "the discrepancy lands in the surcharge line")
        assertEquals(20, quote.percentages.peakPct)
    }

    @Test
    fun a_server_estimate_with_no_surcharge_renders_as_all_base() {
        val response = FareEstimateResponse(
            fareEstimateToken = "tok_02",
            amountMinor = 32_000,
            breakdown = FareBreakdown(firstKmMinor = 8_000, perKmMinor = 6_000, distanceKm = 5.0),
        )

        val quote = FareCalculator.of(response, RideVehicleType.MOTORBIKE)

        assertEquals(32_000L, quote.baseMinor)
        assertEquals(0L, quote.surchargeMinor)
        assertTrue(!quote.percentages.isSurcharged)
    }
}
