package lk.mageride.driver.capture

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import lk.mageride.shared.data.models.registry.CaptureSource

/** Above this, an image is refused rather than uploaded. Matches the gateway's own body ceiling. */
private const val MAX_IMAGE_BYTES = 8 * 1024 * 1024

/**
 * Reads a picked image into memory.
 *
 * On the I/O dispatcher and never on the main thread: a gallery photo off a five-year-old
 * handset's storage is several megabytes, and the same read on the frame-producing thread is a
 * visible stall on exactly the devices this platform is for.
 *
 * A `Uri` from the photo picker is a one-shot grant that dies with the process, which is why the
 * bytes are taken now rather than the `Uri` kept — see [CapturedImage].
 *
 * **The result is always [CaptureSource.GALLERY]**, and it has to be. AL-43 makes a gallery pick
 * the fraud signal the Verification-Officer queue sorts on, and this function is the gallery. An
 * image that came from the SCR-DA-005 scanner arrives through `DocumentCaptureCoordinator`
 * instead and says so itself.
 *
 * @return `null` when the read fails or the file is over [MAX_IMAGE_BYTES]. The caller shows its
 *   own copy; there is nothing here a driver could act on.
 */
@Suppress("TooGenericExceptionCaught")
internal suspend fun readImage(context: Context, uri: Uri, fileName: String): CapturedImage? =
    withContext(Dispatchers.IO) {
        try {
            val bytes = context.contentResolver.openInputStream(uri)?.use { stream ->
                stream.readBytes()
            } ?: return@withContext null

            if (bytes.size > MAX_IMAGE_BYTES) return@withContext null

            CapturedImage(
                fileName = fileName,
                bytes = bytes,
                mimeType = context.contentResolver.getType(uri) ?: "image/jpeg",
                capturedVia = CaptureSource.GALLERY,
            )
        } catch (_: Throwable) {
            null
        }
    }

/**
 * [image] decoded for display, or `null` while the decode is in flight or there is nothing to show.
 *
 * Off the main thread through [DocumentImaging.decode], which also samples the bitmap down — a
 * profile photo is a multi-megapixel JPEG and the avatar it is drawn into is 96 dp, so decoding it
 * at full size on the frame-producing thread would be a visible stall on exactly the handsets the
 * URD NFR-22 floor is about.
 *
 * `rotationDegrees = 0`: an image that reached here came off SCR-DA-005, which has already applied
 * `ImageInfo.rotationDegrees` to the still. Rotating again would lay the driver on their side.
 *
 * Keyed on the [CapturedImage] instance, which compares by identity — see its own KDoc on why it
 * is deliberately not a `data class`. That is the right key here: a retake produces a new instance
 * and re-decodes, and a recomposition with the same one does not.
 */
@Composable
internal fun rememberCapturedBitmap(image: CapturedImage?): ImageBitmap? {
    val decoded by produceState<ImageBitmap?>(initialValue = null, image) {
        value = image?.let { DocumentImaging.decode(it.bytes, rotationDegrees = 0)?.asImageBitmap() }
    }
    return decoded
}
