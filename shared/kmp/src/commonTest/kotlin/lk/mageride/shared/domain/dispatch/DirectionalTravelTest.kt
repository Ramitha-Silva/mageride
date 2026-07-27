package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.dispatch.DirectionalConfig
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

/** D5' §12.1's own defaults — 45°, 2 km, 250 m, two uses, two hours. */
private val D5_CONFIG = DirectionalConfig(
    thetaMaxDeg = 45,
    detourMaxM = 2_000,
    progressMinM = 250,
    maxUsesPerDay = 2,
    maxDurationSec = 2 * 60 * 60,
    clearOnFirstTrip = false,
)

// Colombo, on a roughly north-easterly line out of Fort toward Kadawatha.
private val FORT = GeoPoint(lat = 6.9344, lng = 79.8428)
private val PELIYAGODA = GeoPoint(lat = 6.9600, lng = 79.8900)
private val KADAWATHA = GeoPoint(lat = 7.0000, lng = 79.9500)
private val DEHIWALA = GeoPoint(lat = 6.8500, lng = 79.8650)

/**
 * Directional Travel (DT-01..DT-08, D5' §12).
 *
 * Two things this file is about. First, the predicate is D5' §12.1's, exactly: three tests, all of
 * which must hold, and none of which may be relaxed (DT-05). Second, **every threshold comes from
 * `DirectionalConfig`** — the 45° / 2 km / 250 m are the defaults of an admin-tunable singleton
 * row, and a build that treated them as constants would disagree with dispatch the day an operator
 * moved one.
 */
@OptIn(ExperimentalTime::class)
class DirectionalTravelTest {

    private val predicate = DirectionalPredicate(D5_CONFIG)

    @Test
    fun a_ride_along_the_drivers_own_route_is_kept() {
        val decision = predicate.evaluate(
            driver = FORT,
            destination = KADAWATHA,
            pickup = FORT,
            dropoff = PELIYAGODA,
        )

        assertTrue(decision.eligible, "failed: ${decision.failed}")
        assertEquals(emptySet(), decision.failed)
        assertTrue(decision.progressMetres > D5_CONFIG.progressMinM)
    }

    @Test
    fun a_ride_pointing_the_other_way_fails_the_bearing_test() {
        val decision = predicate.evaluate(
            driver = FORT,
            destination = KADAWATHA,
            pickup = FORT,
            dropoff = DEHIWALA,
        )

        assertFalse(decision.eligible)
        assertContains(decision.failed, DirectionalCriterion.BEARING)
        assertTrue(decision.angularDiffDeg > D5_CONFIG.thetaMaxDeg)
    }

    @Test
    fun a_pickup_beyond_the_detour_ceiling_fails_however_well_it_points() {
        val decision = predicate.evaluate(
            driver = DEHIWALA,
            destination = KADAWATHA,
            pickup = FORT,
            dropoff = PELIYAGODA,
        )

        // The ride still points the driver's way; they are simply too far from the pickup.
        assertFalse(decision.eligible)
        assertFalse(DirectionalCriterion.BEARING in decision.failed)
        assertContains(decision.failed, DirectionalCriterion.DETOUR)
        assertTrue(decision.pickupDetourMetres > D5_CONFIG.detourMaxM)
    }

    @Test
    fun a_ride_that_makes_no_real_progress_fails_even_pointing_the_right_way() {
        // Dropping off 100 m along a 20 km route is the right heading and the wrong ride: the
        // driver would spend the fare and be no closer to where they are going.
        val barelyAhead = GeoPoint(lat = FORT.lat + 0.0009, lng = FORT.lng + 0.0009)

        val decision = predicate.evaluate(
            driver = FORT,
            destination = KADAWATHA,
            pickup = FORT,
            dropoff = barelyAhead,
        )

        assertFalse(decision.eligible)
        assertContains(decision.failed, DirectionalCriterion.PROGRESS)
        assertTrue(decision.progressMetres < D5_CONFIG.progressMinM)
    }

    @Test
    fun the_thresholds_move_with_the_server_config_and_are_not_baked_in() {
        val ride = { p: DirectionalPredicate ->
            p.evaluate(driver = FORT, destination = KADAWATHA, pickup = FORT, dropoff = DEHIWALA)
        }

        assertFalse(ride(predicate).eligible)

        // An operator who widens θ_max to 180° accepts every bearing, and the client has to agree
        // with them — otherwise the driver app explains a filter dispatch is no longer applying.
        val wide = DirectionalPredicate(D5_CONFIG.copy(thetaMaxDeg = 180, progressMinM = -100_000))

        assertTrue(ride(wide).eligible, "failed: ${ride(wide).failed}")
    }

    @Test
    fun the_progress_test_needs_strictly_more_than_the_configured_minimum() {
        // D5' §12.1 writes `dist(dropoff, dest) < dist(pickup, dest) − progress_min` — a strict
        // inequality, so a ride that gains the minimum and not a metre more does not qualify. The
        // two configs below straddle the achieved gain by under a metre, which pins the boundary
        // without pretending a haversine result lands on a whole number.
        val gained = predicate.evaluate(FORT, KADAWATHA, FORT, PELIYAGODA).progressMetres

        val atOrAbove = DirectionalPredicate(D5_CONFIG.copy(progressMinM = ceil(gained).toInt()))
        val justBelow = DirectionalPredicate(D5_CONFIG.copy(progressMinM = floor(gained).toInt() - 1))

        assertContains(atOrAbove.evaluate(FORT, KADAWATHA, FORT, PELIYAGODA).failed, DirectionalCriterion.PROGRESS)
        assertTrue(justBelow.evaluate(FORT, KADAWATHA, FORT, PELIYAGODA).eligible)
    }

    @Test
    fun the_predicate_reports_the_metrics_dispatch_persists_for_the_audit() {
        val decision = predicate.evaluate(
            driver = FORT,
            destination = KADAWATHA,
            pickup = FORT,
            dropoff = PELIYAGODA,
        )

        // R-11 / DT-02: `dispatch.candidate_scores.breakdown.directional`. Support comparing a
        // driver's "why did I not get that ride" against the server's row needs the same numbers.
        assertTrue(decision.angularDiffDeg in 0.0..180.0)
        assertEquals(0.0, decision.pickupDetourMetres, absoluteTolerance = 1.0)
        assertTrue(decision.progressMetres > 0.0)
    }

    // ------------------------------------------------------------------------------------------
    // The driver-facing card (DT-03, DT-04, DT-08)
    // ------------------------------------------------------------------------------------------

    private fun standing(active: Boolean = true, usesRemaining: Int = 1, expiresIn: Duration = 2.hours) =
        DirectionalStanding(
            filter = DirectionalFilterState(
                active = active,
                destination = KADAWATHA,
                label = "home",
                expiresAt = if (active) OFFER_EPOCH + expiresIn else null,
                timeRemainingSec = if (active) expiresIn.inWholeSeconds.toInt() else 0,
                usesRemaining = usesRemaining,
            ),
            config = D5_CONFIG,
        )

    @Test
    fun turning_the_filter_off_by_hand_still_consumes_the_use() {
        val active = standing(usesRemaining = 1)

        val cleared = active.afterManualClear()

        // US-6A.19, DT-03. Without this a driver flicks the filter on for the one offer they want
        // and off again, all day, on two uses.
        assertFalse(cleared.isActive)
        assertEquals(1, cleared.usesRemaining, "the use was spent at activation and is not refunded")
    }

    @Test
    fun going_offline_clears_the_filter_and_refunds_nothing_either() {
        val cleared = standing(usesRemaining = 0).afterGoingOffline()

        assertFalse(cleared.isActive)
        assertEquals(0, cleared.usesRemaining)
        assertFalse(cleared.canActivate(isOnline = true), "DT-03: the day's budget is gone")
    }

    @Test
    fun a_filter_needs_the_driver_online_and_a_use_in_hand() {
        val idle = standing(active = false, usesRemaining = 2)

        assertTrue(idle.canActivate(isOnline = true))
        assertFalse(idle.canActivate(isOnline = false), "`403 not-online` off standby")
        assertFalse(standing(active = true, usesRemaining = 2).canActivate(isOnline = true))
        assertFalse(idle.copy(filter = idle.filter.copy(usesRemaining = 0)).canActivate(isOnline = true))
    }

    @Test
    fun the_card_counts_down_from_the_deadline_and_stops_at_zero() {
        val live = standing(expiresIn = 2.hours)

        assertEquals(2.hours, live.timeRemaining(OFFER_EPOCH))
        assertEquals(30.minutes, live.timeRemaining(OFFER_EPOCH + 90.minutes))
        assertEquals(Duration.ZERO, live.timeRemaining(OFFER_EPOCH + 3.hours))
        assertTrue(live.hasExpired(OFFER_EPOCH + 2.hours))
        assertEquals(0.5, live.elapsedFraction(OFFER_EPOCH + 1.hours), absoluteTolerance = 1e-9)
    }

    @Test
    fun the_ten_minute_warning_fires_once_and_not_after_expiry() {
        val live = standing(expiresIn = 2.hours)

        assertFalse(live.isExpiringSoon(OFFER_EPOCH))
        assertFalse(live.isExpiringSoon(OFFER_EPOCH + 109.minutes))
        // DT-08 / US-10.14: the same threshold the pre-expiry push uses, so the card and the
        // notification agree about when "expiring soon" starts.
        assertTrue(live.isExpiringSoon(OFFER_EPOCH + 110.minutes))
        assertFalse(live.isExpiringSoon(OFFER_EPOCH + 2.hours), "expired is not expiring")
    }

    @Test
    fun an_inactive_filter_falls_back_to_the_servers_own_seconds() {
        val idle = standing(active = false, usesRemaining = 2)

        assertEquals(Duration.ZERO, idle.timeRemaining(OFFER_EPOCH))
        assertFalse(idle.isExpiringSoon(OFFER_EPOCH))
        assertFalse(idle.hasExpired(OFFER_EPOCH))
    }

    @Test
    fun bearings_and_distances_behave_the_way_the_predicate_assumes() {
        assertEquals(0.0, bearingDegrees(FORT, GeoPoint(FORT.lat + 1, FORT.lng)), absoluteTolerance = 0.01)
        assertEquals(90.0, bearingDegrees(GeoPoint(0.0, 0.0), GeoPoint(0.0, 1.0)), absoluteTolerance = 0.01)

        // The wrap-around case: 350° and 10° are twenty degrees apart, not three hundred and forty.
        assertEquals(20.0, angularDifferenceDegrees(350.0, 10.0), absoluteTolerance = 1e-9)
        assertEquals(180.0, angularDifferenceDegrees(0.0, 180.0), absoluteTolerance = 1e-9)
        assertEquals(0.0, distanceMetres(FORT, FORT), absoluteTolerance = 1e-6)

        // One degree of latitude is very close to 111 km anywhere on the sphere.
        assertEquals(
            111_195.0,
            distanceMetres(GeoPoint(0.0, 0.0), GeoPoint(1.0, 0.0)),
            absoluteTolerance = 50.0,
        )
    }
}
