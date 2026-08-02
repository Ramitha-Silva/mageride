package lk.mageride.driver.capture

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sqrt

/**
 * One corner of SCR-DA-005's crop quadrilateral.
 *
 * The order is the winding order — top-left, top-right, bottom-right, bottom-left — and it is the
 * order [CropQuad.corners] answers in. `Matrix.setPolyToPoly` maps source points onto destination
 * points **pairwise**, so a quad whose corners were listed in a different order than the
 * destination rectangle's would produce a mirrored or rotated document rather than a de-skewed one.
 */
internal enum class CropCorner {
    TOP_LEFT,
    TOP_RIGHT,
    BOTTOM_RIGHT,
    BOTTOM_LEFT,
}

/**
 * A point in the captured image, as a fraction of its width and height.
 *
 * Normalised rather than pixels because the same quad is drawn over a viewfinder measured in `dp`
 * and applied to a JPEG measured in megapixels — and the two are never the same size. A quad in
 * pixels would need a conversion at every use, and the one that got forgotten would crop the wrong
 * part of the document on exactly the handsets whose preview and capture resolutions differ most.
 */
internal data class QuadPoint(val x: Float, val y: Float) {

    /** This point clamped into the unit square. A drag can leave the frame; the corner cannot. */
    fun coerced(): QuadPoint = QuadPoint(x.coerceIn(0f, 1f), y.coerceIn(0f, 1f))

    /** Euclidean distance to [other], in normalised units. */
    fun distanceTo(other: QuadPoint): Float = sqrt(square(x - other.x) + square(y - other.y))

    private fun square(value: Float): Float = value * value
}

/**
 * The adjustable crop quadrilateral of **SCR-DA-005** (AL-43).
 *
 * > *"Live camera with an adjustable crop quadrilateral: the driver drags the four corner handles
 * > so the whole document fits the full frame (auto edge-detect proposes a quad; manual drag
 * > overrides). … the cropped, de-skewed image is what gets uploaded."*
 *
 * A quadrilateral and not a rectangle: a licence photographed at an angle is a **trapezium** in the
 * frame, and cropping it with a rectangle keeps the skew that costs Gemini Flash the confidence
 * AL-43 exists to buy back (BR-28.4). The de-skew is `Matrix.setPolyToPoly` over [corners] — see
 * `DocumentImaging`.
 *
 * Pure Kotlin, with no Android type in it, so every rule below is exercised on this build host
 * rather than discovered on a handset.
 */
internal data class CropQuad(
    val topLeft: QuadPoint,
    val topRight: QuadPoint,
    val bottomRight: QuadPoint,
    val bottomLeft: QuadPoint,
) {

    /** The four corners in winding order — the order `setPolyToPoly` needs. */
    val corners: List<QuadPoint> get() = listOf(topLeft, topRight, bottomRight, bottomLeft)

    /** This quad's corner for [corner]. */
    fun corner(corner: CropCorner): QuadPoint = when (corner) {
        CropCorner.TOP_LEFT -> topLeft
        CropCorner.TOP_RIGHT -> topRight
        CropCorner.BOTTOM_RIGHT -> bottomRight
        CropCorner.BOTTOM_LEFT -> bottomLeft
    }

    /**
     * The quad with [corner] dragged to [target].
     *
     * The move is clamped into the frame and **refused outright** when it would leave the quad too
     * thin to be a document — a corner dragged past its neighbour turns the quadrilateral inside
     * out, and `setPolyToPoly` over a self-intersecting quad produces a folded image rather than an
     * error. Refusing is what makes the handle stop at the fold instead of the picture doing
     * something inexplicable.
     */
    fun moved(corner: CropCorner, target: QuadPoint): CropQuad {
        val moved = when (corner) {
            CropCorner.TOP_LEFT -> copy(topLeft = target.coerced())
            CropCorner.TOP_RIGHT -> copy(topRight = target.coerced())
            CropCorner.BOTTOM_RIGHT -> copy(bottomRight = target.coerced())
            CropCorner.BOTTOM_LEFT -> copy(bottomLeft = target.coerced())
        }
        return if (moved.isUsable) moved else this
    }

    /**
     * Whether this quad can be de-skewed at all.
     *
     * Both conditions matter and neither implies the other: a quad can have four long sides and
     * still be a bow-tie (crossed sides), and a convex quad can still be a sliver too thin to
     * carry a legible plate.
     */
    val isUsable: Boolean get() = shortestSide >= MIN_SIDE && isConvex

    /** The shortest of the four sides, in normalised units. */
    private val shortestSide: Float
        get() = listOf(
            topLeft.distanceTo(topRight),
            topRight.distanceTo(bottomRight),
            bottomRight.distanceTo(bottomLeft),
            bottomLeft.distanceTo(topLeft),
        ).min()

    /**
     * Whether the four corners still wind the same way all the way round.
     *
     * The sign of the cross product at each vertex is the turn direction; four turns the same way
     * is a convex quadrilateral, and a sign that flips is the corner that has been dragged past its
     * neighbour. Zero counts as agreeing with everything, so three collinear points are allowed —
     * that is a triangle-ish quad, which [shortestSide] is what rejects.
     */
    private val isConvex: Boolean
        get() {
            val points = corners
            var sign = 0
            for (index in points.indices) {
                val a = points[index]
                val b = points[(index + 1) % points.size]
                val c = points[(index + 2) % points.size]
                val cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x)
                val turn = when {
                    cross > TURN_EPSILON -> 1
                    cross < -TURN_EPSILON -> -1
                    else -> 0
                }
                if (turn == 0) continue
                if (sign == 0) {
                    sign = turn
                } else if (sign != turn) {
                    return false
                }
            }
            return true
        }

    /**
     * The size of the de-skewed image this quad should produce over a source of [sourceWidth] ×
     * [sourceHeight] pixels.
     *
     * Each output side is the **longer** of the two source sides it comes from: a document
     * photographed at an angle has a near edge longer than its far edge, and taking the shorter one
     * would resample the near half downwards and throw away the detail that was actually captured.
     * Bounded so a quad filling a 12 MP frame cannot ask for an allocation the handsets on the
     * Android 8.0 floor do not have.
     */
    fun outputSize(sourceWidth: Int, sourceHeight: Int): Pair<Int, Int> {
        fun span(from: QuadPoint, to: QuadPoint): Float = sqrt(
            ((from.x - to.x) * sourceWidth) * ((from.x - to.x) * sourceWidth) +
                ((from.y - to.y) * sourceHeight) * ((from.y - to.y) * sourceHeight),
        )

        val width = max(span(topLeft, topRight), span(bottomLeft, bottomRight))
        val height = max(span(topLeft, bottomLeft), span(topRight, bottomRight))
        val scale = min(1f, MAX_OUTPUT_EDGE / max(width, height))

        return max(1, (width * scale).roundToInt()) to max(1, (height * scale).roundToInt())
    }

    /** Whether this quad is (near enough) the whole frame — nothing to crop, only to de-skew. */
    val isWholeFrame: Boolean
        get() = corners.zip(FULL.corners).all { (mine, full) ->
            abs(mine.x - full.x) < WHOLE_FRAME_EPSILON && abs(mine.y - full.y) < WHOLE_FRAME_EPSILON
        }

    companion object {

        /**
         * The proposal shown when edge detection found nothing.
         *
         * An inset rectangle rather than the whole frame, because a quad on the frame edge has no
         * handle a thumb can reach — the driver's first drag would be a pan of the corner they
         * could not grab. The inset is also the affordance: a box visibly smaller than the frame
         * says "move me", and one exactly on it says nothing.
         */
        val DEFAULT: CropQuad = inset(DEFAULT_INSET_X, DEFAULT_INSET_Y)

        /** The whole frame. What a de-skew with nothing to crop maps from. */
        val FULL: CropQuad = inset(0f, 0f)

        /** An axis-aligned quad inset by [x] and [y] from each edge. */
        fun inset(x: Float, y: Float): CropQuad = rectangle(x, y, 1f - x, 1f - y)

        /** An axis-aligned quad with these bounds, corners wound top-left first. */
        fun rectangle(left: Float, top: Float, right: Float, bottom: Float): CropQuad = CropQuad(
            topLeft = QuadPoint(left, top).coerced(),
            topRight = QuadPoint(right, top).coerced(),
            bottomRight = QuadPoint(right, bottom).coerced(),
            bottomLeft = QuadPoint(left, bottom).coerced(),
        )

        /** Below this, a side is too short for the quad to be a document. */
        const val MIN_SIDE: Float = 0.12f

        private const val DEFAULT_INSET_X = 0.08f
        private const val DEFAULT_INSET_Y = 0.18f
        private const val TURN_EPSILON = 1e-6f

        /**
         * How near an edge a corner has to be before the quad counts as the whole frame.
         *
         * Two percent, not zero: an edge-detect proposal that lands one sample in from the border
         * *is* the whole frame — there is nothing to crop — and treating it as a finding would
         * tell the driver a document had been detected in a photograph of a wall.
         */
        private const val WHOLE_FRAME_EPSILON = 0.02f

        /**
         * The longest edge a de-skewed image may have, in pixels.
         *
         * 2400 px along the long edge is far past what Gemini Flash needs to read a plate or an
         * expiry date, and it keeps the intermediate `Bitmap` under about 23 MB on ARGB_8888 —
         * which a 1 GB handset on the URD NFR-22 floor can actually allocate.
         */
        private const val MAX_OUTPUT_EDGE = 2400f
    }
}
