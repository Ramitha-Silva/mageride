package lk.mageride.passenger.map

import lk.mageride.shared.data.models.VehicleType
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * MAP-04 — *"smooth marker animation between position updates (interpolation)"*.
 *
 * This is the whole of MAP-04's behaviour, and it is pure Kotlin precisely so it can be asserted
 * on a build host with no GL surface. What is being protected is not the tween itself but the four
 * decisions around it: a first sighting must not glide in from the previous vehicle's position, a
 * batch arriving mid-glide must not snap the marker backwards, a bearing must turn the short way,
 * and a vehicle that left the map must leave it *now* rather than fade.
 */
class MarkerInterpolatorTest {

    private val tuk = MapVehicle("01JVEH0000000000000000001", lat = 6.90, lng = 79.86, type = VehicleType.THREE_WHEELER)

    @Test
    fun a_vehicle_seen_for_the_first_time_is_drawn_where_it_is() {
        val interpolator = MarkerInterpolator()

        interpolator.onFrames(listOf(tuk), nowMs = 0)

        // No glide from nowhere: the marker appears at its position, and the loop can stop at once.
        assertEquals(listOf(tuk), interpolator.markersAt(0))
        assertTrue(interpolator.isSettled(0), "a first sighting has nothing to animate")
    }

    @Test
    fun a_second_batch_glides_over_the_gap_that_separated_them() {
        val interpolator = MarkerInterpolator()
        interpolator.onFrames(listOf(tuk), nowMs = 0)

        // The batch window is 2–8 s (US-7.3); this vehicle reported four seconds later, so it
        // glides for four — continuous motion rather than a sprint and a freeze.
        val moved = tuk.copy(lat = 6.94)
        interpolator.onFrames(listOf(moved), nowMs = 4_000)

        assertEquals(6.90, interpolator.markersAt(4_000).single().lat, ABS_TOLERANCE, "starts where it was")
        assertEquals(6.92, interpolator.markersAt(6_000).single().lat, ABS_TOLERANCE, "halfway at halfway")
        assertEquals(6.94, interpolator.markersAt(8_000).single().lat, ABS_TOLERANCE, "lands on the target")
        assertTrue(interpolator.isSettled(8_000))
        assertTrue(!interpolator.isSettled(6_000), "still animating at halfway")
    }

    @Test
    fun a_batch_arriving_mid_glide_starts_from_where_the_marker_is_drawn() {
        val interpolator = MarkerInterpolator()
        interpolator.onFrames(listOf(tuk), nowMs = 0)
        interpolator.onFrames(listOf(tuk.copy(lat = 6.94)), nowMs = 4_000)

        // Halfway through the glide the next batch lands. Restarting from the previous TARGET
        // would snap the marker forward to 6.94 and then walk it back; restarting from the
        // previous *rendered* position is what keeps the path continuous.
        interpolator.onFrames(listOf(tuk.copy(lat = 6.98)), nowMs = 6_000)

        assertEquals(6.92, interpolator.markersAt(6_000).single().lat, ABS_TOLERANCE, "no jump on reseat")
        assertEquals(6.98, interpolator.markersAt(8_000).single().lat, ABS_TOLERANCE)
    }

    @Test
    fun the_glide_is_clamped_at_both_ends() {
        val interpolator = MarkerInterpolator()
        interpolator.onFrames(listOf(tuk), nowMs = 0)

        // Two batches inside a tenth of a second — a reconnect snapshot immediately followed by
        // the next live batch. Animating over 100 ms is the jump MAP-04 exists to remove, so the
        // glide is stretched to the one-second floor.
        interpolator.onFrames(listOf(tuk.copy(lat = 6.94)), nowMs = 100)
        assertTrue(!interpolator.isSettled(500), "a burst still glides for the minimum")
        assertTrue(interpolator.isSettled(1_100))

        // A vehicle unheard from for a minute was quiet, not slow. fanout-svc drops it at the 60 s
        // freshness window, so gliding a whole minute would draw a path nothing travelled.
        val settled = MarkerInterpolator()
        settled.onFrames(listOf(tuk), nowMs = 0)
        settled.onFrames(listOf(tuk.copy(lat = 6.94)), nowMs = 60_000)
        assertTrue(settled.isSettled(60_000 + MarkerInterpolator.MAX_DURATION_MS))
    }

    @Test
    fun a_vehicle_missing_from_a_batch_leaves_the_map_at_once() {
        val interpolator = MarkerInterpolator()
        val bus = MapVehicle("01JVEH0000000000000000002", lat = 6.91, lng = 79.87, type = VehicleType.BUS)
        interpolator.onFrames(listOf(tuk, bus), nowMs = 0)

        // A vehicle that went on hire (US-7.16), went stale (US-7.17) or whose Mode B share was
        // revoked (D-22) must be gone NOW. A marker that faded out over eight seconds would be
        // eight seconds of showing a vehicle the passenger is not entitled to see.
        interpolator.onFrames(listOf(tuk), nowMs = 4_000)

        assertEquals(listOf(tuk.vehicleId), interpolator.markersAt(4_000).map(MapVehicle::vehicleId))
    }

    @Test
    fun a_bearing_turns_the_short_way_round() {
        // A vehicle crossing due north turns 350° → 10°: twenty degrees right, not three hundred
        // and forty left. The naive linear form spins MAP-06's arrow through a whole rotation
        // every time, which on a coastal road heading north is every few seconds.
        assertEquals(0.0, MarkerInterpolator.interpolateBearing(350.0, 10.0, 0.5), ABS_TOLERANCE)
        assertEquals(355.0, MarkerInterpolator.interpolateBearing(350.0, 10.0, 0.25), ABS_TOLERANCE)
        assertEquals(0.0, MarkerInterpolator.interpolateBearing(10.0, 350.0, 0.5), ABS_TOLERANCE, "and back")

        // The ordinary case is still ordinary.
        assertEquals(135.0, MarkerInterpolator.interpolateBearing(90.0, 180.0, 0.5), ABS_TOLERANCE)

        // Whatever it answers is a legal bearing.
        listOf(0.0, 0.25, 0.5, 0.75, 1.0).forEach { t ->
            val bearing = MarkerInterpolator.interpolateBearing(359.0, 1.0, t)
            assertTrue(bearing >= 0.0 && bearing < FULL_TURN, "bearing $bearing out of range at t=$t")
        }
    }

    @Test
    fun a_bearing_is_interpolated_along_with_the_position() {
        val interpolator = MarkerInterpolator()
        interpolator.onFrames(listOf(tuk.copy(heading = 350.0)), nowMs = 0)
        interpolator.onFrames(listOf(tuk.copy(lat = 6.94, heading = 10.0)), nowMs = 4_000)

        val midway = interpolator.markersAt(6_000).single()
        assertTrue(abs(midway.heading) < ABS_TOLERANCE || abs(midway.heading - FULL_TURN) < ABS_TOLERANCE)
    }

    @Test
    fun clearing_forgets_everything() {
        val interpolator = MarkerInterpolator()
        interpolator.onFrames(listOf(tuk), nowMs = 0)

        interpolator.clear()

        assertEquals(emptyList(), interpolator.markersAt(0))
        assertTrue(interpolator.isSettled(0), "an empty map is settled")
    }

    private companion object {
        /** Six decimal places of a degree is about ten centimetres — well past what a map draws. */
        const val ABS_TOLERANCE = 1e-6
        const val FULL_TURN = 360.0
    }
}
