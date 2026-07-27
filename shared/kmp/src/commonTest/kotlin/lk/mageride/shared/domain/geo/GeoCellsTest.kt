package lk.mageride.shared.domain.geo

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * R-06's numbers, and the fence around them.
 *
 * The correction R-06 records — res-7 + ring(2), not res-8 + ring(1) — is the whole reason this
 * component exists as a separate piece of shared code: both apps and the backend have to agree on
 * the same nineteen cells or a passenger joins groups nothing publishes to.
 */
class GeoCellsTest {

    private val grid = TestH3Grid()

    @Test
    fun the_passenger_view_is_nineteen_cells_at_resolution_seven() {
        val cells = GeoCells.viewCells(grid, COLOMBO_FORT)

        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, cells.size)
        assertEquals(19, cells.size, "R-06 — res-7 + ring(2) covers ~3 km in 19 cells")
        assertTrue(cells.all { it.resolution == 7 }, "fanout-svc publishes to res-7 groups only")
    }

    @Test
    fun the_view_resolution_is_seven_and_never_eight() {
        // The superseded figure. res-8's edge is ~0.46 km, so ring(1) reaches about 1 km — a third
        // of the radius a passenger is promised.
        assertEquals(7, GeoCells.VIEW_RESOLUTION)
        assertEquals(7, GeoView.PASSENGER_3KM.resolution)
        assertEquals(2, GeoView.PASSENGER_3KM.ring)
    }

    @Test
    fun the_intercity_view_is_thirty_seven_cells() {
        val cells = GeoCells.viewCells(grid, COLOMBO_FORT, GeoView.INTERCITY_5KM)

        assertEquals(37, cells.size, "ADD §7.4 — res-7 + ring(3) ≈ 5 km")
        assertEquals(37, GeoView.INTERCITY_5KM.hexagonCellCount)
    }

    @Test
    fun the_expected_cell_count_follows_the_hexagonal_ring_formula() {
        // 1 + 3k(k+1): 7 at k=1, 19 at k=2, 37 at k=3. Asserted rather than hard-coded per view so
        // a new view added later cannot claim a count its ring does not produce.
        GeoView.entries.forEach { view ->
            val k = view.ring
            assertEquals(1 + 3 * k * (k + 1), view.hexagonCellCount, "$view")
            assertEquals(view.hexagonCellCount, GeoCells.viewCells(grid, COLOMBO_FORT, view).size)
        }
    }

    @Test
    fun dispatch_pre_filters_at_resolution_five() {
        val cell = GeoCells.dispatchCell(grid, COLOMBO_FORT)

        assertEquals(5, cell.resolution, "geo:drivers:available:{type}:{res5cell} — D5' §3.1")
        assertEquals(GeoCells.DISPATCH_RESOLUTION, cell.resolution)
    }

    @Test
    fun the_dispatch_pre_filter_reaches_two_rings_out() {
        val cells = GeoCells.dispatchPreFilterCells(grid, COLOMBO_FORT)

        assertEquals(19, cells.size, "ring(1..2) of the res-5 pickup cell")
        assertTrue(cells.all { it.resolution == 5 })
        assertTrue(GeoCells.dispatchCell(grid, COLOMBO_FORT) in cells, "the pickup's own cell is in the set")
    }

    @Test
    fun the_dispatch_cell_is_the_pickup_point_read_at_resolution_five() {
        // The res-7 → res-5 *hierarchy* is H3's own and is asserted against the real library in
        // `AndroidH3GridTest`; the synthetic grid's two lattices are independent, so asserting it
        // here would be asserting a property of the test double.
        assertEquals(
            grid.cellAt(COLOMBO_FORT, GeoCells.DISPATCH_RESOLUTION),
            GeoCells.dispatchCell(grid, COLOMBO_FORT),
        )
    }

    @Test
    fun far_apart_places_do_not_share_a_dispatch_cell() {
        assertTrue(GeoCells.dispatchCell(grid, COLOMBO_FORT) != GeoCells.dispatchCell(grid, KANDY))
    }
}
