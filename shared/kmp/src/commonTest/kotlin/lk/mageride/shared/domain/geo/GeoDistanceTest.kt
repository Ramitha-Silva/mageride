package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Spherical geometry, and the rule the geocell design is easiest to get wrong about.
 *
 * ADD §7.4 step 5: "The H3 cell alone is **never** treated as a final distance bound." The last
 * two tests here are that sentence made executable.
 */
class GeoDistanceTest {

    private val grid = TestH3Grid()

    @Test
    fun a_known_distance_comes_out_right() {
        // Colombo Fort to Kandy is ~94 km great-circle.
        val metres = distanceMetres(COLOMBO_FORT, KANDY)

        assertTrue(abs(metres - 94_000) < 3_000, "expected ~94 km, got ${metres / 1000} km")
    }

    @Test
    fun distance_is_symmetric_and_zero_for_the_same_point() {
        assertEquals(0.0, distanceMetres(COLOMBO_FORT, COLOMBO_FORT))
        assertEquals(
            distanceMetres(COLOMBO_FORT, KANDY),
            distanceMetres(KANDY, COLOMBO_FORT),
            absoluteTolerance = 1e-6,
        )
    }

    @Test
    fun bearing_is_clockwise_from_north() {
        val north = COLOMBO_FORT.copy(lat = COLOMBO_FORT.lat + 0.1)
        val east = COLOMBO_FORT.copy(lng = COLOMBO_FORT.lng + 0.1)

        assertEquals(0.0, bearingDegrees(COLOMBO_FORT, north), absoluteTolerance = 0.01)
        assertEquals(90.0, bearingDegrees(COLOMBO_FORT, east), absoluteTolerance = 0.1)
        assertTrue(bearingDegrees(COLOMBO_FORT, KANDY) in 0.0..90.0, "Kandy is north-east of Colombo")
    }

    @Test
    fun the_angle_between_two_bearings_wraps_around_north() {
        assertEquals(20.0, angularDifferenceDegrees(350.0, 10.0), absoluteTolerance = 1e-9)
        assertEquals(180.0, angularDifferenceDegrees(0.0, 180.0), absoluteTolerance = 1e-9)
        assertEquals(0.0, angularDifferenceDegrees(45.0, 45.0), absoluteTolerance = 1e-9)
    }

    @Test
    fun the_cell_set_is_not_a_distance_bound() {
        // The passenger's 19 cells cover ~3 km, but a cell's own extent pushes some of them
        // further out than that. Subscribing to the set is not the same as "everything within
        // 3 km", which is why an exact post-filter is mandatory rather than an optimisation.
        val centres = GeoCells.viewCells(grid, COLOMBO_FORT).map(grid::center)
        val furthest = centres.maxOf { distanceMetres(COLOMBO_FORT, it) }

        assertTrue(furthest > 3_000, "at least one subscribed cell sits beyond the stated radius")
    }

    @Test
    fun the_exact_post_filter_drops_what_the_cells_let_through() {
        val cells = GeoCells.viewCells(grid, COLOMBO_FORT)
        val candidates = cells.map { grid.center(it) }

        val near = exactWithin(candidates, COLOMBO_FORT, radiusMetres = 3_000.0) { it }

        assertTrue(near.size < candidates.size, "the post-filter removed the far cells")
        assertTrue(near.all { it.distanceMetres <= 3_000.0 })
        assertTrue(near.isNotEmpty(), "the cells around the client survive it")
    }

    @Test
    fun the_post_filter_keeps_the_measured_distance_and_the_input_order() {
        val a = GeoPoint(lat = 6.9271, lng = 79.8612)
        val b = GeoPoint(lat = 6.9371, lng = 79.8612)

        val measured = exactWithin(listOf(b, a), COLOMBO_FORT, radiusMetres = 5_000.0) { it }

        assertEquals(listOf(b, a), measured.map { it.value })
        assertTrue(measured[0].distanceMetres > measured[1].distanceMetres)
    }

    @Test
    fun the_radius_boundary_is_inclusive() {
        val point = GeoPoint(lat = 6.9271, lng = 79.8612)
        val exact = distanceMetres(COLOMBO_FORT, KANDY)

        assertTrue(isWithin(point, COLOMBO_FORT, 0.0))
        assertTrue(isWithin(KANDY, COLOMBO_FORT, exact))
        assertFalse(isWithin(KANDY, COLOMBO_FORT, exact - 1))
    }
}
