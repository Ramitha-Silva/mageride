package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint

/**
 * The four H3 operations this platform needs, and nothing else.
 *
 * **Why this is an interface and not a pure-Kotlin implementation.** H3's grid is defined by
 * constant tables — twenty icosahedron face centres and 122 base cells with their neighbours and
 * rotations — and a client's cell ids must be *bit-identical* to the ones `position-processor-svc`
 * (C039) computes server-side, or a passenger joins `cell:{h3index}` groups nothing publishes to.
 * A re-derived grid that is subtly wrong fails silently and looks like an empty map, so the index
 * maths is delegated to the canonical implementation on each platform rather than reimplemented
 * here. What *is* here — the resolutions, the ring size, the hysteresis, the exact-distance
 * post-filter — is the part the specs actually fix, and it is identical on both platforms.
 *
 * Android binds `com.uber:h3` (JNI over the reference C library). iOS has to bind an H3 built
 * against the same C library; see `lk.mageride.shared.platform.platformH3Grid`.
 *
 * Implementations must be **thread-safe and side-effect free** — the passenger map calls
 * [cellAt] on every position fix.
 */
public interface H3Grid {

    /**
     * The cell containing [point] at [resolution].
     *
     * @param resolution `0..15`. This module uses 7 (passenger view) and 5 (dispatch pre-filter).
     */
    public fun cellAt(point: GeoPoint, resolution: Int): H3Cell

    /**
     * [origin] plus every cell within [k] grid steps — H3's `gridDisk`.
     *
     * A hexagon's disk holds `1 + 3k(k+1)` cells: **19** at `k = 2` (R-06's 3 km passenger view)
     * and 37 at `k = 3`. A disk centred on one of the twelve pentagons holds five fewer per ring;
     * none of them is anywhere near Sri Lanka, but nothing here assumes the count.
     */
    public fun gridDisk(origin: H3Cell, k: Int): Set<H3Cell>

    /** The cell's centre coordinate. */
    public fun center(cell: H3Cell): GeoPoint

    /**
     * The ancestor of [cell] at the coarser [resolution] — how a res-7 view cell maps onto the
     * res-5 dispatch index.
     */
    public fun parent(cell: H3Cell, resolution: Int): H3Cell
}

/**
 * Thrown when geo work is attempted before an [H3Grid] has been bound.
 *
 * The message names the binding rather than the symptom: a missing engine surfaces at the first
 * map open, which is a long way from the app module that forgot to provide it.
 */
public class H3GridUnavailableException(message: String, cause: Throwable? = null) :
    IllegalStateException(message, cause)
