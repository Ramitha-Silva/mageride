package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.DirectionalConfig
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.domain.geo.angularDifferenceDegrees
import lk.mageride.shared.domain.geo.bearingDegrees
import lk.mageride.shared.domain.geo.distanceMetres
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

// Directional Travel — "only offer me rides heading my way" (DT-01..DT-08, D5' §12).
//
// DT-01 sets a destination, DT-02 is the predicate, DT-03 the daily budget, DT-04 expiry and
// clearing, DT-05 the fence, DT-06 the empty-pool guarantee, DT-07 kind-agnosticism, DT-08 the
// driver-facing card and its ten-minute warning.
//
// DIRECTIONAL TRAVEL DOES NOT APPEAR IN THE RIDE STATE MACHINE (ADD Appendix B.2 invariant 7). It
// is a dispatch-svc candidate filter applied before an offer is ever created; a ride's states,
// transitions and invariants are identical whether or not the matched driver had one active. That
// is why nothing in this file touches `domain/ride`.
//
// EVERY THRESHOLD COMES FROM `DirectionalConfig`. The 45° / 2 km / 250 m in D5' §12.1 are the
// *defaults of the admin-configurable singleton row*, not constants. A build that hardcoded them
// would silently disagree with dispatch the first time an operator tuned one, and the disagreement
// would show up as offers the driver was told they should not be getting.

/** The three tests a candidate has to pass (D5' §12.1). */
public enum class DirectionalCriterion {

    /** The ride's bearing is within `θ_max` of the driver's own (default 45°). */
    BEARING,

    /** The pickup is within `detour_max` of the driver (default 2 km). */
    DETOUR,

    /** The dropoff gets `progress_min` closer to the destination than the pickup (default 250 m). */
    PROGRESS,
}

/**
 * One evaluation of the predicate, metrics included.
 *
 * The shape mirrors what dispatch-svc persists to `dispatch.candidate_scores.breakdown.directional`
 * (R-11, DT-02), so a support conversation about "why did I not get that ride" compares like with
 * like.
 *
 * @property eligible Whether the candidate survives. All three criteria must hold.
 * @property failed Which criteria did not. Empty when [eligible].
 * @property angularDiffDeg Angle between the ride's bearing and the driver's, 0–180.
 * @property pickupDetourMetres Driver-to-pickup distance.
 * @property progressMetres How much closer to the destination the dropoff is than the pickup.
 *   Negative when the ride would send the driver backwards.
 */
public data class DirectionalDecision(
    val eligible: Boolean,
    val failed: Set<DirectionalCriterion>,
    val angularDiffDeg: Double,
    val pickupDetourMetres: Double,
    val progressMetres: Double,
)

/**
 * D5' §12.1's predicate.
 *
 * **Advisory on the device.** dispatch-svc runs the authoritative evaluation against the whole
 * candidate set before any offer exists; this copy exists so the driver app can explain the filter
 * — "that ride was 60° off your route" — and so the rule is testable against the spec rather than
 * only against a running service. Nothing here can add a candidate, relax a gate or change what a
 * driver is offered (DT-05).
 *
 * **Kind-agnostic** (DT-07): the predicate takes a pickup and a dropoff and nothing else, so a
 * `passenger`, `proxy` or `package` ride with the same two points evaluates identically. It
 * composes with P-11 package-size compatibility rather than replacing it.
 *
 * @param config The singleton `dispatch.directional_config` row, read from the server.
 */
public class DirectionalPredicate(private val config: DirectionalConfig) {

    /**
     * Whether a driver heading to [destination] should be kept in the round for this ride.
     *
     * @param driver Where the driver is now.
     * @param destination Where their filter points.
     * @param pickup Where the ride starts.
     * @param dropoff Where it ends.
     */
    public fun evaluate(
        driver: GeoPoint,
        destination: GeoPoint,
        pickup: GeoPoint,
        dropoff: GeoPoint,
    ): DirectionalDecision {
        val angular = angularDifferenceDegrees(
            bearingDegrees(driver, destination),
            bearingDegrees(pickup, dropoff),
        )
        val detour = distanceMetres(pickup, driver)
        val progress = distanceMetres(pickup, destination) - distanceMetres(dropoff, destination)

        val failed = buildSet {
            if (angular > config.thetaMaxDeg) add(DirectionalCriterion.BEARING)
            if (detour > config.detourMaxM) add(DirectionalCriterion.DETOUR)
            // Strictly greater: D5' §12.1 writes `dist(dropoff, dest) < dist(pickup, dest) −
            // progress_min`, so a ride that gains exactly the minimum does not qualify.
            if (progress <= config.progressMinM.toDouble()) add(DirectionalCriterion.PROGRESS)
        }

        return DirectionalDecision(
            eligible = failed.isEmpty(),
            failed = failed,
            angularDiffDeg = angular,
            pickupDetourMetres = detour,
            progressMetres = progress,
        )
    }
}

/**
 * A driver's Directional filter, projected for the card (DT-03, DT-04, DT-08).
 *
 * **A use is spent on activation, and turning the filter off does not give it back** (DT-03,
 * US-6A.19). That is the anti-gaming rule: without it a driver could flick the filter on for the
 * one offer they wanted and off again, all day, on two uses. `DELETE /v1/standby/directional`
 * answers with the same `usesRemaining` it had before, and so does this projection.
 *
 * @property filter The server's own view (`GET /v1/standby/directional`).
 * @property config The thresholds and budgets, for the scale the card renders against.
 */
public data class DirectionalStanding(val filter: DirectionalFilterState, val config: DirectionalConfig) {

    /** Whether a filter is live. */
    public val isActive: Boolean get() = filter.active

    /** Activations left today, in Asia/Colombo (D-38). */
    public val usesRemaining: Int get() = filter.usesRemaining

    /** Activations the driver gets per day, so the card can render "1 of 2". */
    public val usesPerDay: Int get() = config.maxUsesPerDay

    /**
     * Whether the driver may set a filter.
     *
     * Being online is the other half (`403 not-online`) and is presence state, not filter state —
     * pass it in rather than guessing from an inactive filter, which is also what an online driver
     * with no filter looks like.
     *
     * @param isOnline Whether the driver is on standby.
     */
    public fun canActivate(isOnline: Boolean): Boolean = isOnline && !isActive && usesRemaining > 0

    /**
     * Time left on the active filter, floored at zero.
     *
     * Prefers `expiresAt` so the countdown keeps running between reads; falls back to the server's
     * `timeRemainingSec` when the filter carries no deadline.
     */
    public fun timeRemaining(now: Timestamp): Duration {
        val deadline = filter.expiresAt ?: return filter.timeRemainingSec.seconds.coerceAtLeast(Duration.ZERO)
        val left = deadline - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    /** How far through the activation we are, `0.0` at the start and `1.0` at expiry. */
    public fun elapsedFraction(now: Timestamp): Double {
        val total = config.maxDurationSec.seconds
        if (total <= Duration.ZERO) return 1.0
        return (1.0 - timeRemaining(now) / total).coerceIn(0.0, 1.0)
    }

    /**
     * Whether the ten-minute pre-expiry warning is due (DT-08, US-10.14).
     *
     * The push is notify-svc's; this is the same threshold, so the card and the notification agree
     * about when "expiring soon" starts.
     */
    public fun isExpiringSoon(now: Timestamp): Boolean =
        isActive && timeRemaining(now) <= PRE_EXPIRY_REMINDER && timeRemaining(now) > Duration.ZERO

    /** Whether the activation has lapsed on this device (DT-04). */
    public fun hasExpired(now: Timestamp): Boolean = isActive && timeRemaining(now) == Duration.ZERO

    /**
     * The standing after the driver clears the filter by hand (`DELETE /v1/standby/directional`).
     *
     * [usesRemaining] is deliberately unchanged — see the class KDoc.
     */
    public fun afterManualClear(): DirectionalStanding = copy(
        filter = filter.copy(active = false, destination = null, expiresAt = null, timeRemainingSec = 0),
    )

    /**
     * The standing after the driver goes offline, or after the broker's LWT fires (DT-04).
     *
     * Same shape as a manual clear, and the use is likewise not refunded: an activation that ended
     * because the driver stopped working was still an activation.
     */
    public fun afterGoingOffline(): DirectionalStanding = afterManualClear()

    public companion object {

        /** How long before expiry the driver is warned (DT-08, US-10.14). */
        public val PRE_EXPIRY_REMINDER: Duration = 10.minutes
    }
}

// ---------------------------------------------------------------------------------------------
// Spherical geometry for the predicate lives in `domain/geo` (C017): `distanceMetres`,
// `bearingDegrees` and `angularDifferenceDegrees`, imported above.
//
// C015 carried its own copies because `domain/geo` was expected to be JNI-backed. It is not — the
// H3 *index* arithmetic is platform-supplied, but the distance and bearing work is plain common
// Kotlin, and DT-02's thresholds and R-06's exact post-filter are the same two formulae. One
// implementation, one set of tests. The server's own evaluation is PostGIS geography, which is an
// ellipsoid: the two agree to well under a metre at the 2 km and 250 m thresholds in play here,
// and dispatch-svc is authoritative regardless.
// ---------------------------------------------------------------------------------------------
