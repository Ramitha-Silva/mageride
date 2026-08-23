package lk.mageride.shared.db.driver

import kotlin.time.Duration
import kotlin.time.Instant

/**
 * One of the driver's documents as this handset holds it (`mobile_db_schema.md` §3.17, Δ MCS-28).
 *
 * @property documentId `registry.documents.id` — a specific version of a specific document, so a
 *   renewed certificate is a different row rather than an overwrite.
 * @property vehicleId `null` for the driving licence, which belongs to the person and not to any
 *   vehicle (AL-27). It is also how a screen decides where to draw this: the null ones belong on
 *   SCR-DA/DI-029 and the rest on the card of the vehicle they name.
 * @property isStale Whether the local lifetime (§0.4 condition 3) has run out.
 *
 *   **Stale is still drawn.** A driver at a checkpoint with no signal is better served by
 *   yesterday's certificate than by an empty screen, so this marks the copy rather than hiding it;
 *   the sweep is what eventually removes it.
 */
public data class CachedDocumentImage(
    val documentId: String,
    val vehicleId: String?,
    val kind: String,
    val side: String?,
    val contentType: String,
    val bytes: ByteArray,
    val version: String?,
    val isStale: Boolean,
) {

    // `ByteArray` is identity-compared by a generated equals, which would make two reads of one
    // document unequal and re-emit state on every refresh.
    override fun equals(other: Any?): Boolean =
        other is CachedDocumentImage &&
            documentId == other.documentId &&
            vehicleId == other.vehicleId &&
            kind == other.kind &&
            side == other.side &&
            contentType == other.contentType &&
            version == other.version &&
            isStale == other.isStale &&
            bytes.contentEquals(other.bytes)

    override fun hashCode(): Int {
        var result = documentId.hashCode()
        result = 31 * result + (vehicleId?.hashCode() ?: 0)
        result = 31 * result + kind.hashCode()
        result = 31 * result + (side?.hashCode() ?: 0)
        result = 31 * result + contentType.hashCode()
        result = 31 * result + (version?.hashCode() ?: 0)
        result = 31 * result + isStale.hashCode()
        result = 31 * result + bytes.contentHashCode()
        return result
    }
}

/**
 * The driver's own documents on disk (§3.17, Δ MCS-28).
 *
 * **This is the §0.4 identity-document exception and it inherits all five of its conditions.** The
 * two that this class is responsible for are the bounded lifetime — [retention], applied on write
 * and swept by [forgetExpired] — and the fact that nothing here ever leaves the sandbox. The other
 * three are the server's (own documents only), the database file's (encrypted at rest) and §0.4's
 * own wipe rule (logout, device-revoke, erasure).
 *
 * **Why it is worth holding these at all.** The moment a driver is asked for a licence or an
 * insurance certificate is the moment they are least likely to have a connection — a checkpoint, a
 * depot gate, the side of a road. A screen that can only show a document with signal is a screen
 * that cannot show it when it matters.
 *
 * **Every method blocks.** SQLDelight's Android and Native drivers are synchronous; call these off
 * the main thread.
 */
public class DocumentImageCache(private val db: DriverDb, private val retention: Duration) {

    /** Everything held for one vehicle — its four onboarding documents, as far as they are cached. */
    public fun forVehicle(vehicleId: String, now: Instant): List<CachedDocumentImage> =
        db.sql.documentImagesQueries.selectForVehicle(vehicleId).executeAsList().map { it.toCached(now) }

    /** The driving licence and anything else belonging to the person rather than a vehicle. */
    public fun personal(now: Instant): List<CachedDocumentImage> =
        db.sql.documentImagesQueries.selectPersonal().executeAsList().map { it.toCached(now) }

    /** Whether the image behind [version] is one this handset still has to fetch. */
    public fun needs(documentId: String, version: String?): Boolean {
        val held = db.sql.documentImagesQueries.select(documentId).executeAsOneOrNull()

        return held == null || version == null || held.version != version
    }

    /** Records one document's bytes, with the local deadline §0.4 condition 3 requires. */
    public fun write(
        documentId: String,
        vehicleId: String?,
        kind: String,
        side: String?,
        contentType: String,
        bytes: ByteArray,
        version: String?,
        now: Instant,
    ) {
        db.transaction {
            db.sql.documentImagesQueries.upsert(
                document_id = documentId,
                vehicle_id = vehicleId,
                kind = kind,
                side = side,
                content_type = contentType,
                bytes = bytes,
                version = version,
                cached_at = now,
                expires_at = now + retention,
            )
        }
    }

    /**
     * §0.4 condition 3, as a sweep.
     *
     * Run from the same retention pass as everything else in §4 rather than on a timer of its own:
     * a deadline that only fires while a screen is open is not a deadline.
     */
    public fun forgetExpired(now: Instant) {
        db.transaction { db.sql.documentImagesQueries.deleteExpired(now) }
    }

    /**
     * Forgets one vehicle's documents.
     *
     * The whole file is wiped on logout and erasure (§0.4 condition 4), so this is for the narrower
     * case that rule does not cover: a driver who no longer operates a vehicle whose documents this
     * handset is still holding.
     */
    public fun forgetVehicle(vehicleId: String) {
        db.transaction { db.sql.documentImagesQueries.deleteForVehicle(vehicleId) }
    }

    /** Forgets every cached document. */
    public fun clear() {
        db.transaction { db.sql.documentImagesQueries.deleteAll() }
    }

    private fun Document_images.toCached(now: Instant): CachedDocumentImage = CachedDocumentImage(
        documentId = document_id,
        vehicleId = vehicle_id,
        kind = kind,
        side = side,
        contentType = content_type,
        bytes = bytes,
        version = version,
        isStale = expires_at <= now,
    )
}
