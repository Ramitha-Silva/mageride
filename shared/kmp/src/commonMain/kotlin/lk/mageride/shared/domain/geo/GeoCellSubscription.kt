package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.Duration

/**
 * What changed about a client's geocell group membership.
 *
 * [join] and [leave] are what a caller passes to `JoinGeocells` / `LeaveGeocells`; sending
 * [cells] wholesale on every fix is what the hysteresis exists to prevent. A crossing of one
 * res-7 boundary moves five or six of the nineteen cells, not all nineteen.
 *
 * @property cells Everything the client should now be subscribed to.
 * @property join Cells to add — empty when nothing changed.
 * @property leave Cells to drop.
 * @property heldUntil When a *deferred* crossing may be applied, or `null` when nothing is being
 *   held back. Non-null means the client moved into a new cell and the 30 s window has not
 *   elapsed, so membership deliberately did not change.
 */
public data class CellSubscriptionUpdate(
    public val cells: Set<H3Cell>,
    public val join: Set<H3Cell>,
    public val leave: Set<H3Cell>,
    public val heldUntil: Timestamp? = null,
) {
    /** Whether the caller has anything to send. */
    public val changed: Boolean get() = join.isNotEmpty() || leave.isNotEmpty()

    /** Whether a boundary crossing is being suppressed right now. */
    public val isHeld: Boolean get() = heldUntil != null
}

/**
 * A client's live-map subscription, with the 30 s boundary hysteresis (ADD §7.4 step 6).
 *
 * The device feeds it position fixes; it answers with the group deltas to send. Three rules,
 * all of them from the spec rather than from taste:
 *
 * 1. **Staying in the same cell changes nothing.** The nineteen cells are recomputed only when
 *    the client's own cell changes.
 * 2. **A crossing is applied at most once every [GeoCells.BOUNDARY_HYSTERESIS].** The first
 *    crossing after the window lapses is applied immediately; anything sooner is held, and the
 *    update says so through [CellSubscriptionUpdate.heldUntil].
 * 3. **Crossing back cancels the held crossing.** This is the case the hysteresis is for: a
 *    passenger standing on a boundary whose fixes alternate between two cells produces no group
 *    churn at all, rather than one deferred change per lapse.
 *
 * **A reconnect is not churn.** [onReconnected] re-joins the whole set without consulting the
 * window — after a socket drop the server holds no membership at all, and D6' §5.4 requires the
 * client to rejoin its groups and resync from `GET /v1/nearby`. Rate-limiting *that* would leave
 * the map blank for up to thirty seconds.
 *
 * Not thread-safe: drive it from one coroutine, like the position stream it follows.
 *
 * @param grid The platform H3 engine.
 * @param view Which radius the client is showing.
 * @param hysteresis Overridable for tests; the default is the spec's 30 s.
 */
public class GeoCellSubscription(
    private val grid: H3Grid,
    private val view: GeoView = GeoView.PASSENGER_3KM,
    private val hysteresis: Duration = GeoCells.BOUNDARY_HYSTERESIS,
) {
    private var currentCells: Set<H3Cell> = emptySet()
    private var currentAnchor: H3Cell? = null
    private var appliedAt: Timestamp? = null
    private var heldAnchor: H3Cell? = null

    /** Everything the client is subscribed to right now. */
    public val cells: Set<H3Cell> get() = currentCells

    /** The client's own cell — the centre of [cells]. */
    public val anchor: H3Cell? get() = currentAnchor

    /** The cell the client has moved into but whose crossing is being held back, if any. */
    public val pendingAnchor: H3Cell? get() = heldAnchor

    /**
     * Feeds a position fix.
     *
     * @param point Where the client is now.
     * @param now Wall clock — supplied rather than read so a test can drive the window.
     */
    public fun onPosition(point: GeoPoint, now: Timestamp): CellSubscriptionUpdate =
        onCell(grid.cellAt(point, view.resolution), now)

    /**
     * Re-evaluates a held crossing without a new fix.
     *
     * A client that crosses a boundary and then stops moving still has to end up subscribed to
     * the right cells; call this from the same timer that drives the map.
     */
    public fun refresh(now: Timestamp): CellSubscriptionUpdate {
        val held = heldAnchor ?: return unchanged()
        return onCell(held, now)
    }

    /**
     * The full set to re-join after the socket came back (D6' §5.4).
     *
     * Deliberately not subject to the hysteresis — see the class KDoc. The result's
     * [CellSubscriptionUpdate.leave] is always empty: the server dropped every group when the
     * connection went, so there is nothing to leave.
     */
    public fun onReconnected(): CellSubscriptionUpdate =
        CellSubscriptionUpdate(cells = currentCells, join = currentCells, leave = emptySet())

    /** Forgets everything — the map was closed, or the user signed out. */
    public fun reset() {
        currentCells = emptySet()
        currentAnchor = null
        appliedAt = null
        heldAnchor = null
    }

    // Three returns, one per rule in the class KDoc. Folding them into one expression would hide
    // which of the three fired.
    @Suppress("ReturnCount")
    private fun onCell(cell: H3Cell, now: Timestamp): CellSubscriptionUpdate {
        val anchorNow = currentAnchor
        if (anchorNow == cell) {
            // Rule 3: back inside the cell we are already serving. Whatever was held is moot.
            heldAnchor = null
            return unchanged()
        }

        val lastChange = appliedAt
        val releaseAt = lastChange?.plus(hysteresis)
        if (anchorNow != null && releaseAt != null && now < releaseAt) {
            heldAnchor = cell
            return unchanged(heldUntil = releaseAt)
        }

        return apply(cell, now)
    }

    private fun apply(cell: H3Cell, now: Timestamp): CellSubscriptionUpdate {
        val next = grid.gridDisk(cell, view.ring)
        val update = CellSubscriptionUpdate(
            cells = next,
            join = next - currentCells,
            leave = currentCells - next,
        )
        currentCells = next
        currentAnchor = cell
        appliedAt = now
        heldAnchor = null
        return update
    }

    private fun unchanged(heldUntil: Timestamp? = null): CellSubscriptionUpdate =
        CellSubscriptionUpdate(cells = currentCells, join = emptySet(), leave = emptySet(), heldUntil = heldUntil)
}
