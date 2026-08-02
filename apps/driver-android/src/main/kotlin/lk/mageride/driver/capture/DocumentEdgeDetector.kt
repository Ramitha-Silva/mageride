package lk.mageride.driver.capture

import kotlin.math.abs

/**
 * A frame reduced to brightness, for [DocumentEdgeDetector].
 *
 * A grid of 0…255 luminances rather than a `Bitmap` on purpose: nothing about finding a document's
 * border needs colour, an Android `Bitmap` cannot be built in a local unit test, and the whole
 * detector is then exercisable on this build host against a picture written by a `for` loop.
 *
 * @property width Samples per row.
 * @property height Rows.
 * @property samples Row-major luminance, `width * height` long.
 */
internal class LuminanceGrid(val width: Int, val height: Int, val samples: IntArray) {

    init {
        require(width > 0 && height > 0) { "an empty frame has no edges" }
        require(samples.size == width * height) { "expected ${width * height} samples, got ${samples.size}" }
    }

    /** The luminance at ([x], [y]). */
    operator fun get(x: Int, y: Int): Int = samples[y * width + x]
}

/**
 * SCR-DA-005's *"auto edge-detect proposes a quad"* (AL-43, BR-28.4).
 *
 * **What this is, precisely.** A document photographed on a desk, a seat or a dashboard is a patch
 * of one brightness inside a background of another. This finds the background — the median of the
 * frame's outer ring, which is the part of the picture a document in the middle is not in — and
 * then takes the bounding box of everything that differs from it by more than [CONTRAST], from
 * per-row and per-column profiles rather than from single pixels, so one bright reflection cannot
 * drag a whole edge with it.
 *
 * **What it is not.** It proposes an *axis-aligned* box; it does not find the four corners of a
 * skewed document. That is deliberate rather than unfinished: BR-28.4 makes the manual drag the
 * authority (*"auto edge-detect proposes a quad; manual drag overrides"*), and a proposal that
 * guesses a skew wrongly is worse than one that is honestly square — the driver has to drag every
 * corner back rather than only the ones that are out. Fitting a true quadrilateral wants a Hough
 * transform over a Canny edge map, which is OpenCV's job, and OpenCV is a 40 MB native dependency
 * this app does not otherwise need. Recorded in the C069 handoff.
 *
 * Returns `null` rather than a guess when the frame gives it nothing to work with — an even
 * background with no document in it, or a document that already fills the frame. The screen then
 * shows [CropQuad.DEFAULT], which says "put the document in this box" instead of claiming to have
 * found one.
 */
internal object DocumentEdgeDetector {

    /** The quad proposed for [grid], or `null` when nothing document-shaped stood out. */
    @Suppress("ReturnCount")
    fun propose(grid: LuminanceGrid): CropQuad? {
        if (grid.width < MIN_SAMPLES || grid.height < MIN_SAMPLES) return null

        val background = backgroundOf(grid)
        val rows = IntArray(grid.height)
        val columns = IntArray(grid.width)

        for (y in 0 until grid.height) {
            for (x in 0 until grid.width) {
                if (abs(grid[x, y] - background) > CONTRAST) {
                    rows[y]++
                    columns[x]++
                }
            }
        }

        val vertical = span(rows, grid.width) ?: return null
        val horizontal = span(columns, grid.height) ?: return null

        val quad = CropQuad.rectangle(
            left = horizontal.first / grid.width.toFloat(),
            top = vertical.first / grid.height.toFloat(),
            right = (horizontal.last + 1) / grid.width.toFloat(),
            bottom = (vertical.last + 1) / grid.height.toFloat(),
        )

        // A box that is the whole frame is not a finding — it is what you get from a picture of a
        // wall, and proposing it would tell the driver the document had been detected when the
        // detector has nothing. Same for one too small to be a document held up to a camera.
        return quad.takeIf { it.isUsable && !it.isWholeFrame }
    }

    /**
     * The background brightness: the **median** of the frame's outer ring.
     *
     * Median rather than mean because the ring is exactly where a document that overruns the frame,
     * a thumb, or the dark edge of a car seat shows up, and a mean would let any of the three move
     * the threshold enough to lose the real border.
     */
    private fun backgroundOf(grid: LuminanceGrid): Int {
        val ring = ArrayList<Int>((grid.width + grid.height) * 2)
        for (x in 0 until grid.width) {
            ring += grid[x, 0]
            ring += grid[x, grid.height - 1]
        }
        for (y in 0 until grid.height) {
            ring += grid[0, y]
            ring += grid[grid.width - 1, y]
        }
        ring.sort()
        return ring[ring.size / 2]
    }

    /**
     * The first and last index of [profile] that carries enough contrasting samples to be part of
     * the document, or `null` when none does.
     *
     * The floor is a fraction of the profile's own peak rather than an absolute count, because a
     * revenue licence held close fills most of the frame and one held at arm's length fills a
     * quarter of it — an absolute threshold would find one and not the other.
     */
    private fun span(profile: IntArray, crossSection: Int): IntRange? {
        val peak = profile.max()
        if (peak < crossSection * MIN_PEAK_FRACTION) return null

        val floor = (peak * EDGE_FRACTION).toInt().coerceAtLeast(1)
        val first = profile.indexOfFirst { it >= floor }
        val last = profile.indexOfLast { it >= floor }

        return if (first < 0 || last <= first) null else first..last
    }

    /** Below this many samples on a side, the grid is too coarse to say anything. */
    private const val MIN_SAMPLES = 16

    /** How far a sample must be from the background to count as document. */
    private const val CONTRAST = 28

    /** A row or column is inside the document once it holds this fraction of the peak count. */
    private const val EDGE_FRACTION = 0.4f

    /** Below this fraction of a full line, the "document" is noise. */
    private const val MIN_PEAK_FRACTION = 0.2f
}
