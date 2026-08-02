package lk.mageride.driver.capture

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.PaintFlagsDrawFilter
import androidx.core.graphics.createBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.min

/**
 * The bitmap half of **SCR-DA-005** — decode what CameraX captured, sample it for
 * [DocumentEdgeDetector], and produce the perspective-corrected image AL-43 uploads.
 *
 * Everything here is Android graphics and none of it is reachable from a local unit test (`Bitmap`
 * is a stub whose every member answers a default). That is the reason the two *decisions* — where
 * the corners are, and what the output should measure — live in [CropQuad] and
 * [DocumentEdgeDetector] instead, which are pure Kotlin and are tested. What is left here is
 * mechanical.
 *
 * All of it runs on [Dispatchers.Default]: a 12 MP decode and a full-frame perspective warp on the
 * frame-producing thread is a visible freeze on the Android 8.0 handsets this platform is for.
 */
internal object DocumentImaging {

    /**
     * Decodes a captured JPEG, applying the sensor rotation and shrinking it to something a
     * budget handset can warp.
     *
     * `inSampleSize` rather than a decode-then-scale: it is the only form that never allocates the
     * full-resolution bitmap in the first place, which on a 1 GB device is the difference between
     * a scanner and an `OutOfMemoryError`.
     *
     * @param rotationDegrees `ImageInfo.rotationDegrees` — how far the sensor was from upright.
     * @return `null` when the bytes are not a decodable image.
     */
    suspend fun decode(bytes: ByteArray, rotationDegrees: Int): Bitmap? = withContext(Dispatchers.Default) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@withContext null

        val options = BitmapFactory.Options().apply {
            inSampleSize = sampleSizeFor(bounds.outWidth, bounds.outHeight)
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }
        val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
            ?: return@withContext null

        if (rotationDegrees % FULL_TURN == 0) decoded else decoded.rotated(rotationDegrees)
    }

    /**
     * Samples [bitmap] down to a luminance grid for the edge-detect proposal.
     *
     * Coarse on purpose. The proposal is a box, not a boundary; sampling every pixel of a 3 MP
     * frame to find it would cost more than the capture did, and the grid it produced would carry
     * exactly the single-pixel noise the row and column profiles exist to average out.
     */
    fun luminanceGrid(bitmap: Bitmap, samplesOnLongEdge: Int = GRID_SAMPLES): LuminanceGrid {
        val scale = samplesOnLongEdge.toFloat() / max(bitmap.width, bitmap.height)
        val width = max(1, (bitmap.width * scale).toInt())
        val height = max(1, (bitmap.height * scale).toInt())
        val samples = IntArray(width * height)

        for (y in 0 until height) {
            val sourceY = ((y + HALF) * bitmap.height / height).toInt().coerceIn(0, bitmap.height - 1)
            for (x in 0 until width) {
                val sourceX = ((x + HALF) * bitmap.width / width).toInt().coerceIn(0, bitmap.width - 1)
                samples[y * width + x] = luminanceOf(bitmap.getPixel(sourceX, sourceY))
            }
        }

        return LuminanceGrid(width, height, samples)
    }

    /**
     * The AL-43 payload: [quad] cut out of [bitmap], de-skewed, as a JPEG.
     *
     * `Matrix.setPolyToPoly` with four point pairs **is** the perspective transform — it solves for
     * the homography that carries the driver's four corners onto the corners of an upright
     * rectangle, which is what turns a licence photographed at an angle into one photographed
     * square on. `FILTER_BITMAP_FLAG` matters here rather than being a nicety: nearest-neighbour
     * resampling of a warped plate is what makes a `7` read as a `1`.
     */
    suspend fun deskew(bitmap: Bitmap, quad: CropQuad, quality: Int = JPEG_QUALITY): ByteArray? =
        withContext(Dispatchers.Default) {
            val (width, height) = quad.outputSize(bitmap.width, bitmap.height)

            val source = FloatArray(SOURCE_POINTS)
            quad.corners.forEachIndexed { index, point ->
                source[index * 2] = point.x * bitmap.width
                source[index * 2 + 1] = point.y * bitmap.height
            }
            val destination = floatArrayOf(
                0f,
                0f,
                width.toFloat(),
                0f,
                width.toFloat(),
                height.toFloat(),
                0f,
                height.toFloat(),
            )

            val matrix = Matrix()
            if (!matrix.setPolyToPoly(source, 0, destination, 0, CORNERS)) return@withContext null

            val output = createBitmap(width, height)
            Canvas(output).apply {
                // The document may not reach a corner of the output on a very skewed capture; white
                // is what a scanner leaves there, and it is also what OCR expects behind text.
                drawColor(Color.WHITE)
                drawFilter = PaintFlagsDrawFilter(0, Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG)
                drawBitmap(bitmap, matrix, Paint(Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG))
            }

            ByteArrayOutputStream().use { stream ->
                output.compress(Bitmap.CompressFormat.JPEG, quality, stream)
                output.recycle()
                stream.toByteArray()
            }
        }

    /** Rec. 709 luma, in integers. Colour tells this detector nothing a document's edge needs. */
    private fun luminanceOf(pixel: Int): Int =
        (RED_WEIGHT * Color.red(pixel) + GREEN_WEIGHT * Color.green(pixel) + BLUE_WEIGHT * Color.blue(pixel)).toInt()

    private fun Bitmap.rotated(degrees: Int): Bitmap {
        val matrix = Matrix().apply { postRotate(degrees.toFloat()) }
        val rotated = Bitmap.createBitmap(this, 0, 0, width, height, matrix, true)
        if (rotated !== this) recycle()
        return rotated
    }

    /** The smallest power of two that brings the longer edge under [MAX_DECODED_EDGE]. */
    private fun sampleSizeFor(width: Int, height: Int): Int {
        var sample = 1
        while (max(width, height) / sample > MAX_DECODED_EDGE) sample *= 2
        return min(sample, MAX_SAMPLE_SIZE)
    }

    private const val FULL_TURN = 360
    private const val CORNERS = 4
    private const val SOURCE_POINTS = CORNERS * 2
    private const val GRID_SAMPLES = 96
    private const val HALF = 0.5f
    private const val MAX_DECODED_EDGE = 2400
    private const val MAX_SAMPLE_SIZE = 8

    /**
     * 92, not 100. The image is going to Gemini Flash after a redaction pre-pass (D-36), and the
     * difference between 92 and 100 is invisible to OCR and about a third of the bytes — which on
     * a 3G connection in a car park is the difference between an upload that finishes and one the
     * driver cancels.
     */
    private const val JPEG_QUALITY = 92

    private const val RED_WEIGHT = 0.2126
    private const val GREEN_WEIGHT = 0.7152
    private const val BLUE_WEIGHT = 0.0722
}
