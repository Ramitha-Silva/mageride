package lk.mageride.driver.documents

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import lk.mageride.driver.di.DriverDatabase
import lk.mageride.driver.profile.signedLinkParameters
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.DriverDocument
import lk.mageride.shared.db.driver.CachedDocumentImage
import lk.mageride.shared.db.driver.DocumentImageCache
import lk.mageride.shared.db.driver.DocumentImageWrite
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.days

/**
 * The driver's own documents, on disk and refreshed behind them (Δ MCS-28).
 *
 * **This is the §0.4 identity-document exception in an app**, so the fences it is responsible for
 * are worth naming at the seam rather than only in the spec: nothing here leaves the sandbox, the
 * bytes go into the encrypted database and nowhere else, and [DOCUMENT_RETENTION] is the local
 * lifetime that makes holding them proportionate.
 *
 * The reason for holding them at all is the whole of MCS-28: the moment a driver is asked for a
 * licence or an insurance certificate is the moment they are least likely to have a connection.
 */
internal interface DriverDocumentStore {

    /** What is on disk now — drawn first, on the frame the screen opens. */
    suspend fun cached(): List<CachedDocumentImage>

    /**
     * Lists the driver's documents and fetches any image this handset does not already hold.
     *
     * Returns what is on disk afterwards, so a caller draws one list rather than merging two.
     */
    suspend fun refresh(): List<CachedDocumentImage>

    /** Drops anything past its local deadline (§0.4 condition 3). */
    suspend fun sweep()
}

/** [DriverDocumentStore] over §3.17 and registry-svc. */
internal class ApiDriverDocumentStore(
    private val registry: RegistryApi,
    private val database: DriverDatabase,
) : DriverDocumentStore {

    override suspend fun cached(): List<CachedDocumentImage> = onCache { it.all(Clock.System.now()) }

    override suspend fun refresh(): List<CachedDocumentImage> {
        val listed = registry.listDriverDocuments().items

        // Fetched one at a time rather than concurrently, deliberately. These are photographs on a
        // connection that is a Colombo bus at 7am, and four parallel image downloads on it is how a
        // screen that was merely slow becomes a screen that times out.
        listed.forEach { document -> fetchIfMissing(document) }

        return cached()
    }

    override suspend fun sweep() {
        onCache { it.forgetExpired(Clock.System.now()) }
    }

    private suspend fun fetchIfMissing(document: DriverDocument) {
        val query = signedLinkParameters(document.imageUrl)
        val version = query["v"]
        val expires = query["expires"]?.toLongOrNull()
        val signature = query["signature"]
        val driverId = query["d"]

        // One condition, because the answer to every part of it is the same: leave what is on disk
        // alone. `needs` is asked last so a link this build cannot parse costs no database read.
        val worthFetching = expires != null && signature != null && driverId != null &&
            onCache { it.needs(document.docId, version) }

        if (!worthFetching) return

        val bytes = registry.getDriverDocumentImage(document.docId, driverId, version.orEmpty(), expires, signature)

        onCache {
            it.write(
                DocumentImageWrite(
                    documentId = document.docId,
                    vehicleId = document.vehicleId,
                    kind = document.kind.wire,
                    // The contract carries no side on this row: the licence's two images are two
                    // documents of the same kind, and which is which is the officer queue's
                    // question rather than this screen's.
                    side = null,
                    contentType = CONTENT_TYPE,
                    bytes = bytes,
                    version = version,
                ),
                Clock.System.now(),
            )
        }
    }

    private suspend fun <T> onCache(block: (DocumentImageCache) -> T): T = withContext(Dispatchers.IO) {
        block(DocumentImageCache(database.get(), DOCUMENT_RETENTION))
    }
}

/**
 * How long a cached document image lives on the handset (§0.4 condition 3, Δ MCS-28).
 *
 * **No spec fixes this number, and it is deliberately far shorter than NFR-28's ninety days.** That
 * window governs the *server's* copy, which is evidence; this is a convenience copy on a device
 * somebody can lose. Thirty days is long enough that a driver who has not opened the app in a few
 * weeks still has their licence at a checkpoint, and short enough that a handset which stops being
 * used stops holding an NIC fairly quickly.
 */
internal val DOCUMENT_RETENTION: Duration = 30.days

/**
 * What the platform is told these bytes are.
 *
 * Every document on this surface arrives as a photograph taken on a handset or a scan of one, and
 * the contract's own response type is `image/*`. A stored `content_type` exists so a viewer does
 * not have to guess, not because this app can offer a better answer than the one it was sent.
 */
private const val CONTENT_TYPE = "image/jpeg"
