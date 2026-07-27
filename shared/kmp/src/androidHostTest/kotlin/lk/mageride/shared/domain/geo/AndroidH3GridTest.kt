package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.platform.platformH3Grid
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * The real H3 engine — `com.uber:h3` over the reference C library.
 *
 * **This is the test the definition of done names**: "the computed cell set for a reference
 * coordinate matches the 19-cell res-7 + ring(2) expectation". `GeoCellsTest` proves the *rules*
 * on a synthetic hex grid so they hold on every target; this proves the rules against the
 * implementation whose cell ids the backend actually publishes to.
 *
 * The golden set below was taken from `com.uber:h3` 4.4.0 for Colombo Fort and is asserted
 * verbatim: if a library upgrade ever moved a cell id, every SignalR group name on the platform
 * would move with it, and that has to be a build failure rather than a silent empty map.
 */
class AndroidH3GridTest {

    private val grid = assertNotNull(platformH3Grid(), "Android must have an H3 engine")

    @Test
    fun the_reference_coordinate_resolves_to_the_expected_res_seven_cell() {
        val cell = grid.cellAt(COLOMBO_FORT, GeoCells.VIEW_RESOLUTION)

        assertEquals(COLOMBO_FORT_RES7, cell.token)
        assertEquals(7, cell.resolution, "read from the index bits, not from the library")
        assertTrue(cell.isWellFormed)
    }

    @Test
    fun the_passenger_view_is_the_nineteen_cell_golden_set() {
        val cells = GeoCells.viewCells(grid, COLOMBO_FORT)

        assertEquals(19, cells.size)
        assertEquals(COLOMBO_FORT_RING2, cells.map { it.token }.toSet())
        assertTrue(cells.all { it.resolution == 7 })
        assertTrue(grid.cellAt(COLOMBO_FORT, 7) in cells, "the client's own cell is in its own view")
    }

    @Test
    fun the_wider_intercity_view_is_thirty_seven_cells() {
        assertEquals(37, GeoCells.viewCells(grid, COLOMBO_FORT, GeoView.INTERCITY_5KM).size)
    }

    @Test
    fun the_dispatch_cell_is_the_res_five_ancestor_of_the_view_cell() {
        val dispatch = GeoCells.dispatchCell(grid, COLOMBO_FORT)

        assertEquals(COLOMBO_FORT_RES5, dispatch.token)
        assertEquals(5, dispatch.resolution)
        assertEquals(dispatch, grid.parent(grid.cellAt(COLOMBO_FORT, 7), 5))
    }

    @Test
    fun the_cell_centre_is_inside_the_cell() {
        val cell = grid.cellAt(COLOMBO_FORT, GeoCells.VIEW_RESOLUTION)

        assertEquals(cell, grid.cellAt(grid.center(cell), GeoCells.VIEW_RESOLUTION))
        assertTrue(distanceMetres(COLOMBO_FORT, grid.center(cell)) < 2_000, "a res-7 hexagon is ~1.2 km across")
    }

    @Test
    fun the_hysteresis_holds_a_real_boundary_crossing() {
        // Same rule as GeoCellSubscriptionTest, driven by real cell geometry: 1.5 km east of
        // Colombo Fort is a different res-7 cell.
        val subscription = GeoCellSubscription(grid)
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        val east = GeoPoint(lat = COLOMBO_FORT.lat, lng = COLOMBO_FORT.lng + 0.02)

        val held = subscription.onPosition(east, GEO_EPOCH + 20.seconds)
        assertTrue(held.isHeld, "$east must be a different cell for this test to mean anything")

        val applied = subscription.onPosition(east, GEO_EPOCH + 31.seconds)
        assertTrue(applied.changed)
        assertEquals(19, applied.cells.size)
    }

    @Test
    fun the_index_layout_this_module_reads_agrees_with_the_library() {
        // H3Cell parses resolution and the base cell out of the raw index in common code; if that
        // ever drifted from the library's own layout, every group name would still *look* right.
        listOf(0, 3, 5, 7, 9, 12, 15).forEach { resolution ->
            val cell = grid.cellAt(KANDY, resolution)

            assertEquals(resolution, cell.resolution, "res $resolution")
            assertTrue(cell.isWellFormed, cell.token)
            assertEquals(cell, H3Cell.parse(cell.token))
        }
    }

    private companion object {
        const val COLOMBO_FORT_RES7 = "87611cb11ffffff"
        const val COLOMBO_FORT_RES5 = "85611cb3fffffff"

        /** `gridDisk(latLngToCell(6.9271, 79.8612, 7), 2)` — R-06's 19 cells. */
        val COLOMBO_FORT_RING2 = setOf(
            "87611cb11ffffff",
            "87611cb10ffffff",
            "87611cb13ffffff",
            "87611cb1effffff",
            "87611cb1cffffff",
            "87611cb02ffffff",
            "87611cb15ffffff",
            "87611cb14ffffff",
            "87611cb16ffffff",
            "87611cb12ffffff",
            "87611cbacffffff",
            "87611cbadffffff",
            "87611cb1affffff",
            "87611cb18ffffff",
            "87611cb1dffffff",
            "87611cb03ffffff",
            "87611cb00ffffff",
            "87611cb06ffffff",
            "87611cb33ffffff",
        )
    }
}
