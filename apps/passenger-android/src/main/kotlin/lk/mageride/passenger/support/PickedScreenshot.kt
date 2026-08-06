package lk.mageride.passenger.support

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * A screenshot the passenger picked, held in memory until Submit uploads it.
 *
 * **Bytes, not a `Uri`.** The system photo picker's grant dies with the pick and does not survive a
 * process death, so a `Uri` parked in view-model state would be a permission failure at the moment
 * the ticket is submitted — which is the one moment it must not fail. Reading now costs a few
 * hundred kilobytes of heap for the life of one sheet.
 *
 * @property fileName What the `docs.uploads` row is called. Not user-facing, and deliberately not
 *   the file's own name: `IObjectStore`'s key is built from ids the platform minted, never from a
 *   client filename (backend/CLAUDE.md).
 * @property bytes The image as written.
 * @property mimeType What the content resolver said it was.
 */
internal data class PickedScreenshot(val fileName: String, val bytes: ByteArray, val mimeType: String) {

    // `ByteArray` is compared by identity, which would make two copies of one screenshot look like
    // two different attachments in a `copy(...)`-driven state. Generated members are overridden
    // rather than left to the compiler's default for that reason alone.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PickedScreenshot) return false
        return fileName == other.fileName && mimeType == other.mimeType && bytes.contentEquals(other.bytes)
    }

    override fun hashCode(): Int {
        var result = fileName.hashCode()
        result = result * HASH_STEP + mimeType.hashCode()
        return result * HASH_STEP + bytes.contentHashCode()
    }

    private companion object {
        const val HASH_STEP = 31
    }
}

/**
 * Reads a picked image off the content resolver, or answers `null`.
 *
 * `null` for anything that did not come back cleanly — a revoked grant, a stream that failed, or a
 * file past [MAX_SCREENSHOT_BYTES]. The sheet then simply has no attachment, which is the same
 * outcome as never having picked one; `POST /v1/support/screenshots` answers `413
 * payload-too-large` above its own ceiling and the check here is what keeps a ten-megabyte read off
 * the wire to learn that.
 */
internal suspend fun readScreenshot(context: Context, uri: Uri): PickedScreenshot? = withContext(Dispatchers.IO) {
    @Suppress("TooGenericExceptionCaught") // Any failure here means "no attachment", never a crash.
    try {
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return@withContext null
        if (bytes.isEmpty() || bytes.size > MAX_SCREENSHOT_BYTES) return@withContext null

        PickedScreenshot(
            fileName = SCREENSHOT_FILE_NAME,
            bytes = bytes,
            mimeType = context.contentResolver.getType(uri) ?: DEFAULT_MIME,
        )
    } catch (_: Throwable) {
        null
    }
}

/** Five megabytes. A phone screenshot is a fraction of this; anything larger is not one. */
private const val MAX_SCREENSHOT_BYTES = 5 * 1024 * 1024

private const val SCREENSHOT_FILE_NAME = "support-screenshot.jpg"
private const val DEFAULT_MIME = "image/jpeg"
