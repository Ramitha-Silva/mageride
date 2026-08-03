package lk.mageride.driver.wallet

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import androidx.core.content.FileProvider
import java.io.File
import java.io.IOException

/**
 * Where SCR-DA-025's statement download actually goes (US-9A.19).
 *
 * A seam rather than a call inside the view model: writing a file and starting a chooser both need
 * a `Context`, and everything on the other side of this interface is untestable on this host —
 * which is the same split C069 drew around `DocumentImaging` and C070 around `PositionPublisher`.
 * What *is* testable, and what this component asserts, is that the right bytes and the right media
 * type reach it.
 *
 * **Cache plus a per-file read grant, not shared storage.** Saving into Downloads would be
 * `WRITE_EXTERNAL_STORAGE` below API 29 and `MediaStore` above it — two code paths and one more
 * permission — for a file whose whole purpose is to be handed to something else. The chooser lets
 * the driver put it wherever they actually keep documents, and the grant dies with it. The paths
 * the provider may serve are in `res/xml/file_paths.xml` and are exactly this one directory.
 */
internal interface StatementExporter {

    /**
     * Writes [bytes] and offers them to whatever the driver picks.
     *
     * @return `false` when the file could not be written or nothing on the handset can receive it.
     */
    fun share(fileName: String, format: StatementFormat, bytes: ByteArray): Boolean
}

/** [StatementExporter] over the app's cache and the manifest's `FileProvider`. */
internal class AndroidStatementExporter(context: Context) : StatementExporter {

    private val appContext = context.applicationContext

    @Suppress("ReturnCount") // Two independent failures — the write and the chooser — reported apart.
    override fun share(fileName: String, format: StatementFormat, bytes: ByteArray): Boolean {
        val file = write(fileName, bytes) ?: return false
        val uri = FileProvider.getUriForFile(appContext, "${appContext.packageName}$AUTHORITY_SUFFIX", file)

        val send = Intent(Intent.ACTION_SEND).apply {
            type = format.mimeType
            putExtra(Intent.EXTRA_STREAM, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }

        return try {
            // The chooser, not the last app that handled a CSV: a statement is a document the
            // driver decides the destination of, every time.
            appContext.startActivity(
                Intent.createChooser(send, null).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
            true
        } catch (_: ActivityNotFoundException) {
            false
        }
    }

    /**
     * Replaces any previous statement of the same name.
     *
     * Re-exporting the same range must not accumulate copies in the cache, and the newer bytes are
     * always the ones wanted — the ledger only ever gains lines.
     */
    private fun write(fileName: String, bytes: ByteArray): File? = try {
        val directory = File(appContext.cacheDir, DIRECTORY).apply { mkdirs() }
        File(directory, fileName).apply { writeBytes(bytes) }
    } catch (_: IOException) {
        null
    }

    private companion object {
        /** Matches `android:authorities="${applicationId}.files"` in the manifest. */
        const val AUTHORITY_SUFFIX = ".files"

        /** Matches the single `cache-path` in `res/xml/file_paths.xml`. */
        const val DIRECTORY = "statements"
    }
}
