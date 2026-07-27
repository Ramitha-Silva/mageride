package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.RideVehicleType

// The Mode C tariff (D5' §1.1, `fares.tariffs`, AL-09).
//
// MODE A HAS NO FARE AND MODE B HAS NO PER-TRIP FARE (D5' §1.1). Nothing in this package prices a
// bus, a train or a Mode B seat: Mode A is free and Mode B is a monthly subscription
// (domain/subscription). That is why the table is keyed on RideVehicleType — the enum the
// contract already narrows to the eight bookable types — and not on VehicleType.
//
// EVERY NUMBER HERE IS ADMIN-CONFIGURABLE. `fares.tariffs` is versioned by `effective_from` and
// never updated in place (C005), because a completed ride must stay reconcilable against the rate
// that priced it. `D5_DEFAULTS` is the spec's own table for a client that has not read
// `GET /v1/fare/tariffs` yet — a fallback, not a constant.

/**
 * One row of `fares.tariffs`: what a vehicle type costs.
 *
 * @property vehicleType The type this row prices.
 * @property firstKmMinor The charge that **includes** the first kilometre, minor units.
 * @property perKmMinor The rate for every kilometre after the first, minor units.
 * @property peakSurchargePct Peak-window uplift, whole percent. Applied to the base fare, not to
 *   the per-km rate.
 * @property nightSurchargePct Night-window uplift, whole percent. Stacks **additively** with
 *   [peakSurchargePct] (D5' §1.1) — an 07:00 ride in a hypothetical overlapping window pays
 *   `base × (20 + 15)%`, never `base × 1.20 × 1.15`.
 */
public data class Tariff(
    val vehicleType: RideVehicleType,
    val firstKmMinor: Long,
    val perKmMinor: Long,
    val peakSurchargePct: Int = DEFAULT_PEAK_SURCHARGE_PCT,
    val nightSurchargePct: Int = DEFAULT_NIGHT_SURCHARGE_PCT,
) {
    init {
        require(firstKmMinor >= 0) { "firstKmMinor must be non-negative, was $firstKmMinor" }
        require(perKmMinor >= 0) { "perKmMinor must be non-negative, was $perKmMinor" }
        require(peakSurchargePct >= 0) { "peakSurchargePct must be non-negative, was $peakSurchargePct" }
        require(nightSurchargePct >= 0) { "nightSurchargePct must be non-negative, was $nightSurchargePct" }
    }

    /** The first-kilometre charge as money. */
    public val firstKm: Money get() = Money.ofMinor(firstKmMinor)

    /** The per-kilometre rate as money. */
    public val perKm: Money get() = Money.ofMinor(perKmMinor)

    public companion object {

        /** `fares.tariffs.peak_surcharge_pct DEFAULT 20` (C005, D5' §1.1). */
        public const val DEFAULT_PEAK_SURCHARGE_PCT: Int = 20

        /** `fares.tariffs.night_surcharge_pct DEFAULT 15` (C005, D5' §1.1). */
        public const val DEFAULT_NIGHT_SURCHARGE_PCT: Int = 15
    }
}

/**
 * The tariff in force, by vehicle type.
 *
 * A **missing** row is a real state, not an error: `truck` and `mini_truck` are package-delivery
 * types whose rates §20 leaves to admin configuration, so C005 seeds neither and [of] answers
 * `null` until Finance sets one. A client that treated that as zero would quote a free delivery.
 *
 * @param rows The rows the server returned, or [D5_DEFAULTS]' own. A duplicate vehicle type is
 *   rejected: two rows for one type means the caller has flattened two `effective_from` versions
 *   together, and picking one of them here would hide that.
 */
public class TariffTable(rows: List<Tariff>) {

    /** The rows, in the order given. */
    public val rows: List<Tariff> = rows.toList()

    private val byType: Map<RideVehicleType, Tariff> = rows.associateBy { it.vehicleType }

    init {
        require(byType.size == rows.size) {
            "a tariff table holds one row per vehicle type; got ${rows.size} rows for ${byType.size} types"
        }
    }

    /** The types this table can price at all. */
    public val pricedTypes: Set<RideVehicleType> get() = byType.keys

    /** The row for [vehicleType], or `null` when no rate is configured for it. */
    public fun of(vehicleType: RideVehicleType): Tariff? = byType[vehicleType]

    /**
     * The row for [vehicleType].
     *
     * @throws IllegalArgumentException when the type has no configured rate — for a booking flow
     *   prefer [of], which lets a screen say "not available yet" instead of crashing.
     */
    public fun requireOf(vehicleType: RideVehicleType): Tariff =
        requireNotNull(byType[vehicleType]) { "no tariff configured for ${vehicleType.wire}" }

    public companion object {

        /**
         * D5' §1.1's own table, for a client that has not read the server's yet.
         *
         * | Vehicle | 1st km | per km |
         * |---|---|---|
         * | Motorbike | Rs 80 | Rs 60 |
         * | Three-wheeler | Rs 100 | Rs 80 |
         * | Flex | Rs 130 | Rs 90 |
         * | Sedan | Rs 150 | Rs 100 |
         * | Mini Van | Rs 150 | Rs 110 |
         * | Van | Rs 150 | Rs 120 |
         *
         * `truck` and `mini_truck` are deliberately absent — see the class KDoc.
         */
        public val D5_DEFAULTS: TariffTable = TariffTable(
            listOf(
                Tariff(RideVehicleType.MOTORBIKE, firstKmMinor = 8_000, perKmMinor = 6_000),
                Tariff(RideVehicleType.THREE_WHEELER, firstKmMinor = 10_000, perKmMinor = 8_000),
                Tariff(RideVehicleType.FLEX, firstKmMinor = 13_000, perKmMinor = 9_000),
                Tariff(RideVehicleType.SEDAN, firstKmMinor = 15_000, perKmMinor = 10_000),
                Tariff(RideVehicleType.MINI_VAN, firstKmMinor = 15_000, perKmMinor = 11_000),
                Tariff(RideVehicleType.VAN, firstKmMinor = 15_000, perKmMinor = 12_000),
            ),
        )
    }
}
