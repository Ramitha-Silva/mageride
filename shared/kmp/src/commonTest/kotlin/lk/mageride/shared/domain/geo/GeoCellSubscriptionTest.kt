package lk.mageride.shared.domain.geo

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * The 30-second boundary hysteresis (ADD §7.4 step 6, `signalr-hub.md` §2).
 *
 * Every group change is a backplane operation on `fanout-svc`, so a passenger loitering on a cell
 * edge must not be able to generate one per fix. What the hysteresis must *not* do is delay the
 * first join or a reconnect — both would show an empty map for up to half a minute.
 */
class GeoCellSubscriptionTest {

    private val grid = TestH3Grid()

    private fun subscription() = GeoCellSubscription(grid)

    private fun neighbourOf(point: lk.mageride.shared.data.models.GeoPoint) = grid.center(
        grid.gridDisk(grid.cellAt(point, GeoCells.VIEW_RESOLUTION), 1)
            .first { it != grid.cellAt(point, GeoCells.VIEW_RESOLUTION) },
    )

    @Test
    fun the_first_fix_joins_all_nineteen_cells_immediately() {
        val update = subscription().onPosition(COLOMBO_FORT, GEO_EPOCH)

        assertEquals(19, update.join.size)
        assertEquals(19, update.cells.size)
        assertTrue(update.leave.isEmpty())
        assertFalse(update.isHeld, "nothing to hold back — there was no previous membership")
    }

    @Test
    fun staying_in_the_same_cell_changes_nothing() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)

        val nudged = COLOMBO_FORT.copy(lat = COLOMBO_FORT.lat + 0.0001)
        val update = subscription.onPosition(nudged, GEO_EPOCH + 60.seconds)

        assertFalse(update.changed, "the anchor cell did not move")
        assertFalse(update.isHeld)
    }

    @Test
    fun a_crossing_inside_the_window_is_suppressed() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        val before = subscription.cells

        val update = subscription.onPosition(neighbourOf(COLOMBO_FORT), GEO_EPOCH + 29.seconds)

        assertFalse(update.changed, "group churn is suppressed for 30 s after a crossing")
        assertTrue(update.isHeld)
        assertEquals(GEO_EPOCH + 30.seconds, update.heldUntil)
        assertEquals(before, subscription.cells)
    }

    @Test
    fun the_crossing_is_applied_once_the_window_lapses() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        val before = subscription.cells

        val update = subscription.onPosition(neighbourOf(COLOMBO_FORT), GEO_EPOCH + 30.seconds)

        assertTrue(update.changed)
        assertFalse(update.isHeld)
        assertEquals(19, update.cells.size)
        assertTrue(update.join.isNotEmpty() && update.leave.isNotEmpty())
        assertTrue(update.join.size < 19, "one boundary moves part of the set, not all of it")
        assertEquals(before - update.leave + update.join, subscription.cells)
    }

    @Test
    fun a_held_crossing_is_applied_by_a_refresh_when_the_client_stops_moving() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        subscription.onPosition(neighbourOf(COLOMBO_FORT), GEO_EPOCH + 5.seconds)

        assertFalse(subscription.refresh(GEO_EPOCH + 20.seconds).changed, "still inside the window")

        val applied = subscription.refresh(GEO_EPOCH + 31.seconds)

        assertTrue(applied.changed)
        assertNull(subscription.pendingAnchor)
    }

    @Test
    fun crossing_back_cancels_the_held_crossing() {
        // The thrash case the hysteresis exists for: fixes alternating across one edge must
        // produce no group churn at all, not one deferred change per lapse.
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        val settled = subscription.cells

        subscription.onPosition(neighbourOf(COLOMBO_FORT), GEO_EPOCH + 10.seconds)
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH + 20.seconds)

        assertNull(subscription.pendingAnchor)
        assertFalse(subscription.refresh(GEO_EPOCH + 120.seconds).changed)
        assertEquals(settled, subscription.cells)
    }

    @Test
    fun a_reconnect_rejoins_everything_regardless_of_the_window() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)

        val update = subscription.onReconnected()

        assertEquals(19, update.join.size, "the server holds no membership after a drop (D6' §5.4)")
        assertTrue(update.leave.isEmpty())
        assertFalse(update.isHeld)
    }

    @Test
    fun a_reset_forgets_the_subscription() {
        val subscription = subscription()
        subscription.onPosition(COLOMBO_FORT, GEO_EPOCH)
        subscription.reset()

        assertTrue(subscription.cells.isEmpty())
        assertNull(subscription.anchor)
        assertEquals(19, subscription.onPosition(KANDY, GEO_EPOCH + 1.seconds).join.size)
    }
}
