package lk.mageride.shared.domain.fare

import kotlin.math.floor

// D5' §1.3 — rounding & currency, client side.
//
// "Integer minor units throughout (Rs×100); round() = banker's rounding to nearest minor unit; at
//  each additive step is avoided — compute in minor units, single round only where a *pct/100
//  product is taken. No 'nearest 5/10': MageRide bills exact minor units."
//
// Three rules come out of that sentence and all three are enforced here:
//
//  1. ROUNDING IS HALF-TO-EVEN. Not half-up, not half-away-from-zero. Over a large number of fares
//     half-up drifts upward by half a cent per tie; banker's rounding does not, which is why every
//     financial standard picks it and why the ledger and the fare engine have to agree on it.
//  2. NOTHING IS ROUNDED AT AN ADDITIVE STEP. `baseMinor + surchargeMinor` is exact integer
//     addition; only a *product* — a distance times a rate, an amount times a percentage — ever
//     goes near a rounding rule.
//  3. THE RESULT IS AN INTEGER NUMBER OF MINOR UNITS. There is no "round to the nearest Rs 5"
//     anywhere on this platform.
//
// A percentage of an integer is computed as an EXACT RATIONAL, never through a Double.
// `20_000 * 15 / 100` has one right answer and `2e4 * 0.15` is not guaranteed to be it: 0.15 is
// not representable in binary floating point, and a fare that disagreed with the ledger by one
// cent would be a reconciliation ticket rather than a rounding curiosity. The one place a Double
// is unavoidable is the distance product, because a distance genuinely is fractional.

/**
 * The §1.3 rounding rules, as three functions.
 *
 * Every one of them answers in **minor units** and every one of them is total for the inputs a
 * fare can produce. They are `public` because the DoD is stated about them — the fare table, the
 * OnePay surcharge (US-8.11) and the voucher discount (US-9.19) are three different rules that
 * must round the same way, and sharing one implementation is what makes that true rather than
 * hoped for.
 */
public object FareRounding {

    /** `pct/100`, as the §1.1 formula spells it. */
    private const val PERCENT_DENOMINATOR: Long = 100L

    /** `bps/10000` — the basis-point form `billing.voucher_discount_tiers.discount_bps` uses. */
    private const val BASIS_POINT_DENOMINATOR: Long = 10_000L

    /** The tie. Named because detekt is right that a bare `0.5` in a rounding rule deserves one. */
    private const val HALF: Double = 0.5

    /**
     * Rounds a fractional amount of minor units to a whole one, ties to even.
     *
     * Used for exactly one thing in the fare formula — `round(extraKm * perKmMinor)` — because a
     * distance is the only genuinely fractional input a fare has.
     *
     * @param value Minor units, possibly fractional. Must be finite and within [Long] range.
     * @throws IllegalArgumentException if [value] is NaN, infinite or outside [Long].
     */
    public fun roundToMinor(value: Double): Long {
        require(value.isFinite()) { "cannot round a non-finite amount: $value" }
        require(value >= Long.MIN_VALUE.toDouble() && value <= Long.MAX_VALUE.toDouble()) {
            "amount is outside the minor-unit range: $value"
        }
        val whole = floor(value)
        val fraction = value - whole
        val floored = whole.toLong()
        return when {
            fraction > HALF -> floored + 1

            fraction < HALF -> floored

            // The tie. `floor` is the lower of the two candidates, so "round to even" means keep it
            // when it is even and step up when it is odd. floor(-2.5) = -3, which is odd, so -2.5
            // rounds to -2 — the even neighbour, symmetrically with +2.5 → +2.
            else -> if (floored % 2 == 0L) floored else floored + 1
        }
    }

    /**
     * `round(baseMinor * pct / 100)` — the §1.1 surcharge product and the US-8.11 OnePay surcharge.
     *
     * @param baseMinor The amount being uplifted. Non-negative: no fare, and no amount a surcharge
     *   is taken of, is ever negative (D3' §0 exempts only the signed ledger columns).
     * @param pct Whole percent. Non-negative — `fares.tariffs` and `fares.peak_windows` both CHECK
     *   their percentage columns `>= 0` (C005).
     */
    public fun percentOfMinor(baseMinor: Long, pct: Int): Long =
        fractionOfMinor(baseMinor, pct.toLong(), PERCENT_DENOMINATOR)

    /**
     * `round(baseMinor * bps / 10000)` — the bulk-voucher purchase discount (US-9.19, AL-01).
     *
     * Basis points rather than percent because that is the unit the tier is stored and configured
     * in: `1000` bps is 10%, and Finance can set 12.5% without the column growing a decimal.
     *
     * @param baseMinor The voucher's face value. Non-negative.
     * @param bps Basis points. Non-negative.
     */
    public fun basisPointsOfMinor(baseMinor: Long, bps: Int): Long =
        fractionOfMinor(baseMinor, bps.toLong(), BASIS_POINT_DENOMINATOR)

    /**
     * `round(baseMinor * numerator / denominator)` on **exact integers**, ties to even.
     *
     * The product is taken in [Long] before the division, so nothing is lost to an intermediate
     * result: a Rs 10,000,000 amount at 10,000 bps is 10^13, four orders of magnitude inside
     * [Long].
     *
     * @throws IllegalArgumentException on a negative input or a zero denominator.
     */
    public fun fractionOfMinor(baseMinor: Long, numerator: Long, denominator: Long): Long {
        require(baseMinor >= 0) { "amount must be non-negative, was $baseMinor" }
        require(numerator >= 0) { "numerator must be non-negative, was $numerator" }
        require(denominator > 0) { "denominator must be positive, was $denominator" }

        val product = baseMinor * numerator
        val quotient = product / denominator
        val remainder = product % denominator
        val twiceRemainder = remainder * 2
        return when {
            twiceRemainder > denominator -> quotient + 1
            twiceRemainder < denominator -> quotient
            else -> if (quotient % 2 == 0L) quotient else quotient + 1
        }
    }
}
