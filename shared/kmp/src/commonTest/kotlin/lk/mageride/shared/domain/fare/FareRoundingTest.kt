package lk.mageride.shared.domain.fare

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

/**
 * D5' §1.3 — "`round()` = banker's rounding to nearest minor unit".
 *
 * Half-to-even is the claim, so the ties are the test. Half-up and half-to-even agree on every
 * input that is not exactly on the boundary, which is why a test that only checked ordinary
 * numbers would pass against the wrong rule.
 */
class FareRoundingTest {

    // ----------------------------------------------------------------------------------------
    // Doubles — the distance product, `round(extraKm * perKmMinor)`
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_fraction_below_a_half_rounds_down_and_above_it_rounds_up() {
        assertEquals(7L, FareRounding.roundToMinor(7.49))
        assertEquals(8L, FareRounding.roundToMinor(7.51))
        assertEquals(0L, FareRounding.roundToMinor(0.0))
        assertEquals(24_000L, FareRounding.roundToMinor(24_000.0))
    }

    @Test
    fun a_tie_rounds_to_the_even_neighbour_not_upwards() {
        // Half-up would answer 3 and 5 here. Over a large number of fares that half-cent per tie
        // is a systematic drift away from the ledger, which is why §1.3 names banker's rounding.
        assertEquals(2L, FareRounding.roundToMinor(2.5))
        assertEquals(4L, FareRounding.roundToMinor(3.5))
        assertEquals(4L, FareRounding.roundToMinor(4.5))
        assertEquals(6L, FareRounding.roundToMinor(5.5))
        assertEquals(0L, FareRounding.roundToMinor(0.5))
    }

    @Test
    fun the_tie_rule_is_symmetric_about_zero() {
        // Not reachable from a fare, which is never negative — but a rounding rule that behaved
        // differently on each side of zero would be the wrong rule, and a refund line is signed.
        assertEquals(-2L, FareRounding.roundToMinor(-2.5))
        assertEquals(-4L, FareRounding.roundToMinor(-3.5))
    }

    @Test
    fun a_non_finite_or_out_of_range_amount_is_refused() {
        assertFailsWith<IllegalArgumentException> { FareRounding.roundToMinor(Double.NaN) }
        assertFailsWith<IllegalArgumentException> { FareRounding.roundToMinor(Double.POSITIVE_INFINITY) }
        assertFailsWith<IllegalArgumentException> { FareRounding.roundToMinor(1e19) }
    }

    // ----------------------------------------------------------------------------------------
    // Exact rationals — the surcharge product, `round(baseMinor * pct / 100)`
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_percentage_of_a_whole_amount_is_exact() {
        assertEquals(5_200L, FareRounding.percentOfMinor(26_000, 20))
        assertEquals(3_900L, FareRounding.percentOfMinor(26_000, 15))
        assertEquals(36_750L, FareRounding.percentOfMinor(105_000, 35))
        assertEquals(0L, FareRounding.percentOfMinor(26_000, 0))
        assertEquals(0L, FareRounding.percentOfMinor(0, 20))
    }

    @Test
    fun a_percentage_that_lands_on_a_half_minor_unit_rounds_to_even() {
        // Rs 4.90 at OnePay's 5% is 24.5 minor units; Rs 5.10 is 25.5. Half-up would answer 25 and
        // 26; banker's rounding answers 24 and 26, and the two errors cancel across a day's takings.
        assertEquals(24L, FareRounding.percentOfMinor(490, 5))
        assertEquals(26L, FareRounding.percentOfMinor(510, 5))
        assertEquals(400L, FareRounding.percentOfMinor(8_010, 5))
    }

    @Test
    fun a_percentage_is_computed_as_a_rational_not_through_a_double() {
        // 0.15 is not representable in binary floating point: `20_000 * 0.15` is 3000.0000000000005
        // on every IEEE-754 platform, and a `floor` of that is still 3000 — but the same expression
        // at other magnitudes lands the other side. The integer path has no such failure mode.
        assertEquals(3_000L, FareRounding.percentOfMinor(20_000, 15))
        assertEquals(1_000_000_000_000L, FareRounding.percentOfMinor(10_000_000_000_000L, 10))
    }

    @Test
    fun a_negative_input_or_a_zero_denominator_is_refused() {
        assertFailsWith<IllegalArgumentException> { FareRounding.percentOfMinor(-1, 5) }
        assertFailsWith<IllegalArgumentException> { FareRounding.percentOfMinor(100, -5) }
        assertFailsWith<IllegalArgumentException> { FareRounding.fractionOfMinor(100, 5, 0) }
    }

    // ----------------------------------------------------------------------------------------
    // Basis points — the voucher discount, `round(denomination * bps / 10000)`
    // ----------------------------------------------------------------------------------------

    @Test
    fun basis_points_are_the_same_rule_at_a_finer_scale() {
        // US-9.19's worked example: a Rs 1,000 voucher at 10% costs Rs 900.
        assertEquals(10_000L, FareRounding.basisPointsOfMinor(100_000, 1_000))
        assertEquals(12_500L, FareRounding.basisPointsOfMinor(100_000, 1_250))
        assertEquals(0L, FareRounding.basisPointsOfMinor(100_000, 0))
    }

    @Test
    fun a_basis_point_tie_also_rounds_to_even() {
        // 5 minor units at 5,000 bps is 2.5 — the tie — and 15 at 5,000 bps is 7.5.
        assertEquals(2L, FareRounding.basisPointsOfMinor(5, 5_000))
        assertEquals(8L, FareRounding.basisPointsOfMinor(15, 5_000))
    }
}
