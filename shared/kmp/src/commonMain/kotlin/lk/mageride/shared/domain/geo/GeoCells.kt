package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

// Which cells a client subscribes to, and which cells dispatch searches.
//
// R-06 IS A CORRECTION, AND THE WRONG VALUE IS STILL IN CIRCULATION. Earlier ADD text claimed
// "res-8 + ring(1) ≈ 3 km"; res-8's edge is ~0.46 km, so ring(1) covers about 1 km — a passenger
// would see a third of the vehicles they should. The corrected figure is res-7 + ring(2) = 19
// cells ≈ 2.8–3.3 km (ADD §7.4 step 4, D5' §3.1, signalr-hub.md §2). Nothing in this module may
// name resolution 8, and `GeoRealtimeHygieneTest` fails the build if it does.

/**
 * How much of the map a client watches at once.
 *
 * The two views differ only in ring size: the resolution is fixed at 7 because the SignalR groups
 * `fanout-svc` publishes to are res-7 groups, so a client asking at another resolution is asking
 * for groups that do not exist.
 *
 * @property resolution Always [GeoCells.VIEW_RESOLUTION] — H3 res 7.
 * @property ring How many grid steps out from the client's own cell.
 */
public enum class GeoView(public val resolution: Int, public val ring: Int) {

    /** The default passenger live map — ~3 km, 19 cells (R-06, US-7.3). */
    PASSENGER_3KM(GeoCells.VIEW_RESOLUTION, 2),

    /** The wider intercity view — ~5 km, 37 cells (ADD §7.4 step 4). */
    INTERCITY_5KM(GeoCells.VIEW_RESOLUTION, 3),
    ;

    /**
     * How many cells the view holds when its centre is a hexagon: `1 + 3k(k + 1)`.
     *
     * 19 for [PASSENGER_3KM], 37 for [INTERCITY_5KM]. A view centred on one of H3's twelve
     * pentagons holds fewer — see [H3Grid.gridDisk].
     */
    public val hexagonCellCount: Int get() = CENTRE_CELL + CELLS_ADDED_PER_RING * ring * (ring + 1)
}

/** The client's own cell, which every view contains. */
private const val CENTRE_CELL = 1

/** Ring `k` of a hexagonal tiling holds `6k` cells, so a disk of `k` adds `3k(k + 1)`. */
private const val CELLS_ADDED_PER_RING = 3

/** The geocell rules of ADD §7.4 / D5' §3.1 / §5.4, in one place. */
public object GeoCells {

    /**
     * H3 resolution 7 — ~5.16 km² per hexagon, ~1.22 km edge.
     *
     * This is the fan-out granularity: `cell:{h3index}` SignalR groups and the `cell.{h3index}`
     * Redpanda topics are res-7 (ADD §7.4 steps 1–3).
     */
    public const val VIEW_RESOLUTION: Int = 7

    /**
     * H3 resolution 5 — ~252 km² per hexagon.
     *
     * The dispatch driver index is keyed `geo:drivers:available:{type}:{res5cell}` and
     * `dispatch-svc` scans `ring(1..2)` of the pickup's res-5 cell as a **coarse pre-filter**
     * (D5' §3.1). The client mirrors it only to explain a search area; the exact-distance
     * post-filter is still mandatory — see [exactWithin].
     */
    public const val DISPATCH_RESOLUTION: Int = 5

    /** How far dispatch's coarse pre-filter reaches out from the pickup cell (D5' §3.1). */
    public const val DISPATCH_PRE_FILTER_RING: Int = 2

    /** The 19 cells R-06 fixes for the 3 km passenger view — asserted, never assumed. */
    public const val PASSENGER_VIEW_CELL_COUNT: Int = 19

    /**
     * How long group membership is held still after a boundary crossing (ADD §7.4 step 6,
     * signalr-hub.md §2).
     *
     * A passenger walking along a cell edge would otherwise join and leave the same six groups
     * every few seconds, and every one of those is a `RemoveFromGroupAsync` on the backplane.
     */
    public val BOUNDARY_HYSTERESIS: Duration = 30.seconds

    /**
     * The cells a client at [centre] subscribes to.
     *
     * @param grid The platform H3 engine.
     * @param centre Where the client is.
     * @param view Which radius, default the R-06 3 km one.
     */
    public fun viewCells(grid: H3Grid, centre: GeoPoint, view: GeoView = GeoView.PASSENGER_3KM): Set<H3Cell> =
        grid.gridDisk(grid.cellAt(centre, view.resolution), view.ring)

    /** The res-5 cell a pickup falls in — the key of the dispatch driver index (D5' §3.1). */
    public fun dispatchCell(grid: H3Grid, point: GeoPoint): H3Cell = grid.cellAt(point, DISPATCH_RESOLUTION)

    /**
     * The coarse candidate cells dispatch would scan for a pickup.
     *
     * **Never a distance bound.** Two res-5 rings reach roughly 45 km; what makes a candidate
     * "near" is the `ST_DWithin` post-filter that runs after, mirrored here by [exactWithin].
     */
    public fun dispatchPreFilterCells(
        grid: H3Grid,
        pickup: GeoPoint,
        ring: Int = DISPATCH_PRE_FILTER_RING,
    ): Set<H3Cell> = grid.gridDisk(dispatchCell(grid, pickup), ring)
}
