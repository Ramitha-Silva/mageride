package lk.mageride.shared.domain.fare

import kotlinx.datetime.LocalTime
import kotlinx.datetime.TimeZone
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.util.BusinessCalendar

// The peak and night windows (D5' §1.1, `fares.peak_windows`).
//
// EVALUATED IN ASIA/COLOMBO (D-38). The column is a `TIME`, not a `TIMESTAMPTZ`, because these are
// recurring daily windows rather than instants — and the whole rule turns on which local wall
// clock a ride falls under. An 18:00 Colombo ride is 12:30Z; a client comparing UTC would price it
// off-peak.
//
// A WINDOW MAY WRAP MIDNIGHT. Night runs 22:00–05:00, so `end_local < start_local` is legal and
// the C005 DDL says so in a column comment rather than forbidding it with a CHECK. Every predicate
// here handles both orientations.
//
// THE PERCENTAGE COMES FROM THE TARIFF, NOT FROM THE WINDOW. `fares.peak_windows` carries a
// `multiplier_pct` column *and* `fares.tariffs` carries `peak_surcharge_pct` / `night_surcharge_pct`
// — two copies of the same 20/15. D5' §1.1's formula reads the tariff
// (`peakPct = isPeak(rideTime) ? tariff.peak_surcharge_pct : 0`), so a window here decides only
// WHETHER an uplift applies and a [Tariff] decides how much. Modelling the window's own percentage
// would give the client a second, silently divergent source. See the C016 handoff.

/** Which uplift a window carries. */
public enum class SurchargeKind {

    /** Rush hour — 07:00–09:00 and 17:00–19:00 by default (`fares.peak_windows`, §20 seed). */
    PEAK,

    /** Overnight — 22:00–05:00 by default. The window that wraps midnight. */
    NIGHT,
}

/**
 * One row of `fares.peak_windows`: a recurring Colombo wall-clock interval.
 *
 * The interval is **half-open**, `[start, end)`. Neither spec pins the boundary, and half-open is
 * the only choice under which two adjacent windows cannot both claim the same instant — with
 * 07:00–09:00 and 09:00–11:00 closed at both ends, 09:00 would be counted twice. See the C016
 * handoff.
 *
 * @property kind Peak or night.
 * @property startLocal Colombo wall-clock start, inclusive.
 * @property endLocal Colombo wall-clock end, exclusive. May be **earlier** than [startLocal], which
 *   means the window wraps midnight.
 */
public data class SurchargeWindow(val kind: SurchargeKind, val startLocal: LocalTime, val endLocal: LocalTime) {

    /** Whether this window wraps past midnight — `22:00–05:00` does, `07:00–09:00` does not. */
    public val wrapsMidnight: Boolean get() = endLocal < startLocal

    /** Whether [local] falls inside the window. */
    public fun contains(local: LocalTime): Boolean =
        if (wrapsMidnight) local >= startLocal || local < endLocal else local >= startLocal && local < endLocal
}

/**
 * The configured windows, as one predicate per kind.
 *
 * @param windows Every row of `fares.peak_windows` the server returned. Order is irrelevant and
 *   overlapping rows of the same kind are harmless — a time inside two peak windows is peak once,
 *   because [isPeak] answers a boolean and D5' §1.1 multiplies by the tariff's single percentage.
 */
public class SurchargeWindows(windows: List<SurchargeWindow>) {

    /** The rows, in the order given. */
    public val windows: List<SurchargeWindow> = windows.toList()

    /** Whether [at] falls in a peak window, evaluated in [zone]. */
    public fun isPeak(at: Timestamp, zone: TimeZone = BusinessCalendar.ZONE): Boolean =
        matches(SurchargeKind.PEAK, at, zone)

    /** Whether [at] falls in a night window, evaluated in [zone]. */
    public fun isNight(at: Timestamp, zone: TimeZone = BusinessCalendar.ZONE): Boolean =
        matches(SurchargeKind.NIGHT, at, zone)

    /**
     * The percentages [tariff] contributes at [at] — the `peakPct` and `nightPct` of §1.1.
     *
     * Zero for a kind whose window [at] is outside of, which is what makes the sum in
     * [FareCalculator] a single rounded product rather than two.
     */
    public fun percentagesAt(
        tariff: Tariff,
        at: Timestamp,
        zone: TimeZone = BusinessCalendar.ZONE,
    ): SurchargePercentages = SurchargePercentages(
        peakPct = if (isPeak(at, zone)) tariff.peakSurchargePct else 0,
        nightPct = if (isNight(at, zone)) tariff.nightSurchargePct else 0,
    )

    private fun matches(kind: SurchargeKind, at: Timestamp, zone: TimeZone): Boolean {
        val local = BusinessCalendar.localTime(at, zone)
        return windows.any { it.kind == kind && it.contains(local) }
    }

    public companion object {

        /**
         * The §20 seed, for a client that has not read the server's windows yet.
         *
         * Peak 07:00–09:00 and 17:00–19:00; night 22:00–05:00. Identical to
         * `1901__seed_fares_billing.sql`, minus the `multiplier_pct` column this type deliberately
         * does not model — see the file header.
         */
        public val D5_DEFAULTS: SurchargeWindows = SurchargeWindows(
            listOf(
                SurchargeWindow(SurchargeKind.PEAK, LocalTime(7, 0), LocalTime(9, 0)),
                SurchargeWindow(SurchargeKind.PEAK, LocalTime(17, 0), LocalTime(19, 0)),
                SurchargeWindow(SurchargeKind.NIGHT, LocalTime(22, 0), LocalTime(5, 0)),
            ),
        )
    }
}

/**
 * The two uplifts in force for one ride, before they are applied.
 *
 * They **stack additively** (D5' §1.1): `surchargeMinor = round(baseMinor × (peakPct + nightPct) /
 * 100)`. Compounding them would be a different — and larger — number, and neither spec does it.
 *
 * @property peakPct Peak uplift in force, whole percent. Zero outside a peak window.
 * @property nightPct Night uplift in force, whole percent. Zero outside a night window.
 */
public data class SurchargePercentages(val peakPct: Int, val nightPct: Int) {

    /** The single percentage the §1.1 product is taken at. */
    public val totalPct: Int get() = peakPct + nightPct

    /** Whether any uplift applies at all. */
    public val isSurcharged: Boolean get() = totalPct > 0
}
