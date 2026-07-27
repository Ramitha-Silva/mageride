package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.round
import kotlin.math.sqrt
import kotlin.time.Instant

/** Colombo Fort — the reference coordinate the geo tests measure from. */
internal val COLOMBO_FORT: GeoPoint = GeoPoint(lat = 6.9271, lng = 79.8612)

/** Kandy, 94 km inland — far enough to be several res-5 cells away. */
internal val KANDY: GeoPoint = GeoPoint(lat = 7.2906, lng = 80.6337)

internal val GEO_EPOCH: Timestamp = Instant.parse("2026-07-27T09:00:00Z")

/**
 * A deterministic hexagonal grid for `commonTest`.
 *
 * **Not H3, and it does not pretend to be.** Real cell ids come from the platform library
 * (`AndroidH3GridTest` asserts those against `com.uber:h3`, including the 19-cell golden set for
 * Colombo Fort). What this provides is the one property the *rules* under test depend on: a
 * hexagonal tiling, so `gridDisk(k)` holds exactly `1 + 3k(k + 1)` cells and a boundary crossing
 * moves a realistic handful of them. That lets the ring arithmetic, the join/leave diffs and the
 * 30-second hysteresis be verified on every target, including the iOS ones where no engine is
 * bound at all.
 *
 * Indices are packed into a genuine H3 cell-index *layout* — mode 1, the resolution in bits 52-55,
 * base-7 digits in the low bits — so [H3Cell.resolution] and [H3Cell.isWellFormed] see the same
 * shape they will see in production.
 */
internal class TestH3Grid : H3Grid {

    override fun cellAt(point: GeoPoint, resolution: Int): H3Cell {
        val (q, r) = axialOf(point, resolution)
        val (originQ, originR) = axialOf(REFERENCE, resolution)
        return encode(resolution, q - originQ, r - originR)
    }

    override fun gridDisk(origin: H3Cell, k: Int): Set<H3Cell> {
        val (res, q, r) = decode(origin)
        val cells = LinkedHashSet<H3Cell>()
        for (dq in -k..k) {
            val lower = maxOf(-k, -dq - k)
            val upper = minOf(k, -dq + k)
            for (dr in lower..upper) {
                cells += encode(res, q + dq, r + dr)
            }
        }
        return cells
    }

    override fun center(cell: H3Cell): GeoPoint {
        val (res, relativeQ, relativeR) = decode(cell)
        val (originQ, originR) = axialOf(REFERENCE, res)
        val q = relativeQ + originQ
        val r = relativeR + originR
        val size = sizeOf(res)
        return GeoPoint(
            lat = size * 1.5 * r,
            lng = size * SQRT3 * (q + r / 2.0),
        )
    }

    override fun parent(cell: H3Cell, resolution: Int): H3Cell = cellAt(center(cell), resolution)

    // ------------------------------------------------------------------------------------------

    private fun sizeOf(resolution: Int): Double = RES7_SIZE_DEG * RES_RATIO.pow(GeoCells.VIEW_RESOLUTION - resolution)

    /**
     * Axial coordinates of [point], rounded to the nearest hex.
     *
     * Cells are addressed **relative to [REFERENCE]**, because a fake index has only `resolution`
     * base-7 digits to carry them — 7^7 combinations at res 7 — which is nowhere near enough for
     * absolute world coordinates at a 1 km pitch. Relative to Colombo it is enough for a few
     * hundred kilometres in every direction, which is all any test needs.
     */
    private fun axialOf(point: GeoPoint, resolution: Int): Pair<Int, Int> {
        val size = sizeOf(resolution)
        return axialRound(
            (SQRT3 / 3.0 * point.lng - point.lat / 3.0) / size,
            2.0 / 3.0 * point.lat / size,
        )
    }

    /** How many `(q, r)` pairs fit in a cell of this resolution's digits: `floor(sqrt(7^res))`. */
    private fun spanOf(resolution: Int): Int = sqrt(7.0.pow(resolution)).toInt()

    private fun axialRound(q: Double, r: Double): Pair<Int, Int> {
        val y = -q - r
        var rq = round(q)
        var ry = round(y)
        var rr = round(r)
        val dq = abs(rq - q)
        val dy = abs(ry - y)
        val dr = abs(rr - r)
        when {
            dq > dy && dq > dr -> rq = -ry - rr
            dy > dr -> ry = -rq - rr
            else -> rr = -rq - ry
        }
        return rq.toInt() to rr.toInt()
    }

    private fun encode(resolution: Int, q: Int, r: Int): H3Cell {
        val span = spanOf(resolution)
        val offset = span / 2
        require(q + offset in 0 until span && r + offset in 0 until span) {
            "($q, $r) is outside the test grid's range at resolution $resolution — move the fixture nearer Colombo"
        }
        var packed = ((q + offset).toLong() * span) + (r + offset).toLong()
        var index = (1L shl 59) or (resolution.toLong() shl 52)
        for (digit in H3_MAX_RESOLUTION downTo 1) {
            val shift = (H3_MAX_RESOLUTION - digit) * 3
            val value = if (digit <= resolution) {
                val next = packed % 7
                packed /= 7
                next
            } else {
                7L
            }
            index = index or (value shl shift)
        }
        return H3Cell(index)
    }

    private fun decode(cell: H3Cell): Triple<Int, Int, Int> {
        val resolution = cell.resolution
        val span = spanOf(resolution)
        val offset = span / 2
        var packed = 0L
        for (digit in 1..resolution) {
            val shift = (H3_MAX_RESOLUTION - digit) * 3
            packed = packed * 7 + ((cell.index ushr shift) and 0x7L)
        }
        val q = (packed / span).toInt() - offset
        val r = (packed % span).toInt() - offset
        return Triple(resolution, q, r)
    }

    private companion object {
        val SQRT3 = sqrt(3.0)

        /** The lattice origin — see [axialOf]. */
        val REFERENCE = COLOMBO_FORT

        /** ~1.2 km, the real res-7 edge length, expressed in degrees. */
        const val RES7_SIZE_DEG = 0.011

        /** Roughly H3's per-resolution scale factor (each level is ~7× the area). */
        const val RES_RATIO = 2.6457513110645907
    }
}
