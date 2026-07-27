package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.query.TransportOption
import lk.mageride.shared.data.models.query.TransportOptionKind

// AL-19 — Mode C tiers expose PRICE ONLY before a driver is matched.
//
// "Before dispatch, Mode C private tiers expose the upfront price only — 'minutes away' and
//  'distance to driver' are suppressed (no driver matched yet). ETA/distance appear only after
//  Accept." (D5' BR-23.3, ADD AL-19, URD US-8.2c)
//
// This is a C016 FENCE, so it is enforced by the SHAPE of the type rather than by a rule somebody
// has to remember: [ModeCTier] HAS NO ETA FIELD AND NO DISTANCE FIELD. A pre-match tier screen
// built on it cannot render one, because there is nothing to render. `query.yaml`'s
// `TransportOption` does carry `etaSeconds` — it has to, since the same payload also lists public
// transport, where a departure time is real and known — and [priceOnly] is the projection that
// drops it on the way in.
//
// The rule is about the PRE-MATCH tier list, not about the whole app: once a driver has accepted,
// there is a real vehicle with a real ETA and [arrivalVisible] says so.

/**
 * One bookable Mode C tier, as the pre-booking results may show it.
 *
 * @property vehicleType The tier.
 * @property label Server-rendered display text (`TransportOption.label`). Rendered, not composed
 *   here: user-facing copy is trilingual and belongs to the apps and to content-svc (D-26).
 * @property priceMinor The upfront price, minor units. The **total** — US-8.4 shows nothing else.
 * @property currency Always LKR.
 * @property fareEstimateToken The token that binds this price to a booking, when the tier came
 *   from `GET /v1/fare/estimate`. `POST /v1/rides/request` rejects a stale or forged one with
 *   `400 invalid-fare-token`, which is what stops a client naming its own fare.
 */
public data class ModeCTier(
    val vehicleType: RideVehicleType,
    val label: String,
    val priceMinor: Long,
    val currency: Currency = Currency.LKR,
    val fareEstimateToken: String? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = priceMinor, currency = currency)
}

/**
 * The pre-match tier list, and the one question a screen has to ask before showing an ETA.
 *
 * The whole class is about AL-19. There is deliberately nothing here that produces a tier *with*
 * an arrival time: a matched ride's ETA belongs to the ride screen and comes from the ride's own
 * driver position (`RideDetail.driver`, C017's live position stream), not from a tier row.
 */
public object ModeCTiers {

    /**
     * The private-hire tiers of a `GET /v1/transport-options` response, **price only**.
     *
     * Three things happen here and each is the rule:
     * - public-transport options are dropped — they are a different list with different rules
     *   (AL-18), and mixing them into a tier board would put a bus in the Mode C picker;
     * - `bus` / `train` typed options are dropped even if they arrive marked private, because
     *   [RideVehicleType] has no entry for them (AL-09) and they are not bookable;
     * - `etaSeconds` and everything else about a driver is discarded, because no driver exists yet.
     *
     * An option with no `estimatedFareMinor` is dropped rather than shown at zero: a tier the
     * server could not price is a tier a passenger must not be able to book.
     */
    public fun priceOnly(options: List<TransportOption>): List<ModeCTier> = options.mapNotNull { option ->
        if (option.kind != TransportOptionKind.PRIVATE) return@mapNotNull null
        val vehicleType = option.vehicleType?.let(RideVehicleType::from) ?: return@mapNotNull null
        val priceMinor = option.estimatedFareMinor ?: return@mapNotNull null
        ModeCTier(
            vehicleType = vehicleType,
            label = option.label,
            priceMinor = priceMinor,
            currency = option.currency ?: Currency.LKR,
        )
    }

    /**
     * Whether a driver's ETA and distance may be shown for a ride in [state] (AL-19).
     *
     * `true` exactly when a driver is assigned — from `Accepted` onward, and never in `Requested`,
     * `Matching` or `Offered`. `Offered` is the interesting one: a driver has been *reserved* and
     * their position is knowable, but they have not accepted and may not, so showing "3 minutes
     * away" would promise a passenger a vehicle that is still free to decline.
     *
     * Reads `RideState.isDriverAssigned` (C012) rather than listing states, so a new state added
     * to the aggregate is classified in one place.
     */
    public fun arrivalVisible(state: RideState?): Boolean = state?.isDriverAssigned == true
}
