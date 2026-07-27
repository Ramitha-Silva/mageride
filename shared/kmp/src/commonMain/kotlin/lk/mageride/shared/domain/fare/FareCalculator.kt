package lk.mageride.shared.domain.fare

import kotlinx.datetime.TimeZone
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.fare.FareBreakdown
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.fare.FinalFareResponse
import lk.mageride.shared.util.BusinessCalendar
import kotlin.math.max

// D5' §1.1's master formula, client side.
//
//   extraKm        = max(0, distanceKm - 1.0)
//   baseMinor      = firstKmMinor + round(extraKm * perKmMinor)
//   peakPct        = isPeak(rideTime)  ? tariff.peak_surcharge_pct  : 0
//   nightPct       = isNight(rideTime) ? tariff.night_surcharge_pct : 0
//   surchargeMinor = round(baseMinor * (peakPct + nightPct) / 100)
//   fareMinor      = baseMinor + surchargeMinor
//
// THE CLIENT NEVER COMPUTES THE AUTHORITATIVE FARE (C016 fence). fare-svc prices every ride, and
// `GET /v1/fare/estimate` hands back a `fareEstimateToken` that BINDS the quoted price —
// `POST /v1/rides/request` rejects a stale or forged one with `400 invalid-fare-token`. There is
// therefore no path by which a number computed here can become what a passenger is charged.
//
// What this class is for is the other three jobs a client actually has:
//   1. RENDERING the breakdown the server sent, from the same formula that produced it, so a
//      receipt's lines add up to its total instead of being four unrelated fields.
//   2. EXPLAINING a price — "Rs 480, including a 20% peak surcharge" — without a second round trip.
//   3. PROVING the rounding rules (§1.3) against a spec-derived table, which is this component's
//      definition of done and cannot be proved against a service that does not exist yet.
//
// DISTANCE IS AN INPUT, NEVER A MEASUREMENT MADE HERE (E-04). An estimate uses the router's route
// distance; a final fare uses the Kalman-filtered, accuracy-weighted GPS track that fare-svc
// resamples (D5' §1.2). Both arrive as a number. A client summing raw GPS points would reproduce
// exactly the 5–15% inflation E-04 exists to remove.

/**
 * A fare, decomposed the way D5' §1.1 builds it.
 *
 * **Only [total] is shown to a passenger** (US-8.4, "TOTAL ONLY shown to user"). The rest is for
 * receipts, support and the driver's earnings breakdown.
 *
 * @property vehicleType The tier priced.
 * @property distanceKm The distance the price was taken over.
 * @property baseMinor First-km charge plus the per-km product, minor units.
 * @property surchargeMinor The peak/night uplift, minor units. Zero outside every window.
 * @property percentages Which uplifts were in force.
 */
public data class FareQuote(
    val vehicleType: RideVehicleType,
    val distanceKm: Double,
    val baseMinor: Long,
    val surchargeMinor: Long,
    val percentages: SurchargePercentages,
) {

    /** `baseMinor + surchargeMinor` — exact integer addition, never a rounded step (§1.3). */
    public val totalMinor: Long get() = baseMinor + surchargeMinor

    /** The only figure a passenger sees (US-8.4). */
    public val total: Money get() = Money.ofMinor(totalMinor)

    /** The pre-surcharge fare. */
    public val base: Money get() = Money.ofMinor(baseMinor)

    /** The uplift. */
    public val surcharge: Money get() = Money.ofMinor(surchargeMinor)
}

/**
 * D5' §1.1, over a tariff table and a set of windows.
 *
 * Both are constructor parameters and both default to the spec's own numbers **only** as a
 * fallback for a client that has not read `GET /v1/fare/tariffs` yet. Build one from the config
 * you just read rather than holding a long-lived instance: `fares.tariffs` is versioned and an
 * admin can move it, and a calculator that pinned the launch-time rate would disagree with the
 * server the day one does.
 *
 * @param tariffs The rates in force.
 * @param windows The peak and night windows in force.
 * @param zone The zone the windows are evaluated in. Asia/Colombo, always (D-38); a parameter so a
 *   test can state the rule rather than depend on the host clock.
 */
public class FareCalculator(
    private val tariffs: TariffTable = TariffTable.D5_DEFAULTS,
    private val windows: SurchargeWindows = SurchargeWindows.D5_DEFAULTS,
    private val zone: TimeZone = BusinessCalendar.ZONE,
) {

    /**
     * Prices a ride, or answers `null` when [vehicleType] has no configured rate.
     *
     * `null` is the honest answer for `truck` / `mini_truck` until Finance configures a delivery
     * rate (C005) — a screen should say the tier is unavailable, not quote a free delivery.
     *
     * @param vehicleType The tier being priced.
     * @param distanceKm Route distance for an estimate, Kalman-resampled distance for a final
     *   fare (§1.2). Must be finite and non-negative.
     * @param rideTime The instant the peak/night windows are evaluated at.
     */
    public fun quote(vehicleType: RideVehicleType, distanceKm: Double, rideTime: Timestamp): FareQuote? =
        tariffs.of(vehicleType)?.let { quoteWith(it, distanceKm, rideTime) }

    /**
     * Prices a ride against a tariff the caller already has.
     *
     * @throws IllegalArgumentException if [distanceKm] is negative or not finite.
     */
    public fun quoteWith(tariff: Tariff, distanceKm: Double, rideTime: Timestamp): FareQuote {
        require(distanceKm.isFinite()) { "distanceKm must be finite, was $distanceKm" }
        require(distanceKm >= 0.0) { "distanceKm must be non-negative, was $distanceKm" }

        // "first km included in the first_km charge" (§1.1). A 0.6 km ride pays the first-km charge
        // and nothing more; a 1.0 km ride pays exactly the same.
        val extraKm = max(0.0, distanceKm - FIRST_KM_INCLUDED)
        val baseMinor = tariff.firstKmMinor + FareRounding.roundToMinor(extraKm * tariff.perKmMinor)

        val percentages = windows.percentagesAt(tariff, rideTime, zone)
        val surchargeMinor = FareRounding.percentOfMinor(baseMinor, percentages.totalPct)

        return FareQuote(
            vehicleType = tariff.vehicleType,
            distanceKm = distanceKm,
            baseMinor = baseMinor,
            surchargeMinor = surchargeMinor,
            percentages = percentages,
        )
    }

    /** Which uplifts apply to [tariff] at [rideTime], without pricing anything. */
    public fun percentagesAt(tariff: Tariff, rideTime: Timestamp): SurchargePercentages =
        windows.percentagesAt(tariff, rideTime, zone)

    public companion object {

        /**
         * The first kilometre is inside the first-km charge (§1.1 `max(0, distanceKm - 1.0)`).
         *
         * Not admin-configurable anywhere in the specs: `fares.tariffs` has a `first_km_minor`
         * column and no `first_km_distance` column, so the boundary itself is fixed while its
         * price is not.
         */
        public const val FIRST_KM_INCLUDED: Double = 1.0

        /**
         * The server's estimate, in the same shape [quote] produces.
         *
         * This is the one a booking screen should render: [FareEstimateResponse.amountMinor] is the
         * authoritative number and its `breakdown` is how fare-svc built it. Recomputing the total
         * from the breakdown would be a client-side fare, which is exactly what the fence forbids;
         * the total is taken as given and only the *decomposition* is mirrored.
         */
        public fun of(response: FareEstimateResponse, vehicleType: RideVehicleType): FareQuote =
            fromBreakdown(vehicleType, response.amountMinor, response.breakdown)

        /** The server's final fare, likewise (`POST /v1/fare/calculate`). */
        public fun of(response: FinalFareResponse, vehicleType: RideVehicleType): FareQuote =
            fromBreakdown(vehicleType, response.amountMinor, response.breakdown)

        /**
         * Splits a server total into base and surcharge using the breakdown it came with.
         *
         * The base is rebuilt from the breakdown's **own** `firstKmMinor`, `perKmMinor` and
         * `distanceKm` — the same three inputs fare-svc priced with — and the surcharge is
         * whatever is left of the authoritative total. That ordering matters: `total = base +
         * surcharge` always holds and the total is never recomputed, so a client can never render
         * a figure other than the one the passenger is charged. If a server ever rounded the base
         * differently the discrepancy would land in the surcharge line and be visible, rather than
         * silently changing the total.
         */
        private fun fromBreakdown(
            vehicleType: RideVehicleType,
            amountMinor: Long,
            breakdown: FareBreakdown,
        ): FareQuote {
            val extraKm = max(0.0, breakdown.distanceKm - FIRST_KM_INCLUDED)
            val baseMinor = breakdown.firstKmMinor + FareRounding.roundToMinor(extraKm * breakdown.perKmMinor)
            return FareQuote(
                vehicleType = vehicleType,
                distanceKm = breakdown.distanceKm,
                baseMinor = baseMinor,
                surchargeMinor = amountMinor - baseMinor,
                percentages = SurchargePercentages(
                    peakPct = breakdown.peakSurchargePct ?: 0,
                    nightPct = breakdown.nightSurchargePct ?: 0,
                ),
            )
        }
    }
}
