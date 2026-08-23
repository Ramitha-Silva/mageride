package lk.mageride.shared.db.driver

import kotlin.time.Instant

/**
 * The driver's own identity as the handset last saw it (`mobile_db_schema.md` §3.16, Δ MCS-27).
 *
 * @property name What SCR-DA/DI-029 and -036 print where the driver's name goes.
 * @property level Driver Level 1–3 (D5' §4.2), or `null` if the level read has never answered.
 * @property registration The live vehicle's plate (D-03).
 * @property photoVersion The signed link's opaque `v` — *which* photograph [photoBytes] is.
 * @property photoBytes The avatar itself. The reason this is bytes and not a URL is in
 *   [DriverProfileCache].
 */
public data class CachedDriverProfile(
    val name: String? = null,
    val level: Int? = null,
    val registration: String? = null,
    val photoVersion: String? = null,
    val photoBytes: ByteArray? = null,
    val syncedAt: Instant? = null,
) {

    /** Whether there is anything worth drawing — an empty row is the same as no row. */
    public val isEmpty: Boolean
        get() = name == null && level == null && registration == null && photoBytes == null

    // `ByteArray` is identity-compared by the generated equals, which would make two reads of the
    // same photograph unequal and re-emit state on every refresh.
    override fun equals(other: Any?): Boolean = other is CachedDriverProfile
        && name == other.name
        && level == other.level
        && registration == other.registration
        && photoVersion == other.photoVersion
        && syncedAt == other.syncedAt
        && photoBytes.contentEquals(other.photoBytes)

    override fun hashCode(): Int {
        var result = name?.hashCode() ?: 0
        result = 31 * result + (level ?: 0)
        result = 31 * result + (registration?.hashCode() ?: 0)
        result = 31 * result + (photoVersion?.hashCode() ?: 0)
        result = 31 * result + (syncedAt?.hashCode() ?: 0)
        result = 31 * result + photoBytes.contentHashCode()
        return result
    }
}

/**
 * What the driver headers draw on the frame they open (Δ MCS-27).
 *
 * **The problem this solves is a first frame, not a round trip.** SCR-DA/DI-029 and SCR-DA/DI-036
 * both open on the driver's name, Driver Level, live plate and photograph, and every one of those
 * was a network read — so both drew a placeholder and filled in a second later, on every open, on
 * whatever connection a Colombo bus at 7am has. Reported from a handset.
 *
 * Nothing here is authoritative. Every field is a cache of something a server owns; this decides
 * what is on screen before the reads answer and is replaced by whatever they say.
 *
 * **The photograph is stored as BYTES, and that is the load-bearing decision.** The obvious design
 * caches the URL — and would achieve nothing: the signed link (MCS-25) carries an `expires` that
 * changes on every profile read, and both image caches that would see it key on the URL, so Coil
 * and `URLCache` would miss every single time and re-download an avatar the handset already holds.
 * `photoVersion` is the link's `v`, which changes when and only when the photograph does, so
 * [needsPhoto] is exactly the question "are the bytes I have still the right ones?".
 *
 * **Every method blocks**, like everything else over SQLDelight's Android and Native drivers. Call
 * them off the main thread.
 */
public class DriverProfileCache(private val db: DriverDb) {

    /** What to draw right now, or an empty row for a driver this handset has not seen. */
    public fun read(driverId: String): CachedDriverProfile =
        db.sql.driverProfileQueries.select(driverId).executeAsOneOrNull()?.let { row ->
            CachedDriverProfile(
                name = row.display_name,
                level = row.level,
                registration = row.registration,
                photoVersion = row.photo_version,
                photoBytes = row.photo_bytes,
                syncedAt = row.synced_at,
            )
        } ?: CachedDriverProfile()

    /**
     * Records what a completed profile read answered.
     *
     * A `null` argument means *"this read did not carry that field"* and leaves what is stored
     * alone — never *"the driver has no name"*. The three values arrive from three different calls
     * and one of them failing must not blank the other two.
     */
    public fun writeIdentity(
        driverId: String,
        name: String?,
        level: Int?,
        registration: String?,
        at: Instant,
    ) {
        db.transaction {
            db.sql.driverProfileQueries.upsertIdentity(
                displayName = name,
                level = level,
                registration = registration,
                syncedAt = at,
                driverId = driverId,
            )
        }
    }

    /** Records a photograph whose bytes are actually in hand, and which one it is. */
    public fun writePhoto(driverId: String, version: String?, bytes: ByteArray, at: Instant) {
        db.transaction {
            db.sql.driverProfileQueries.upsertPhoto(
                photoUrl = null,
                photoVersion = version,
                photoBytes = bytes,
                syncedAt = at,
                driverId = driverId,
            )
        }
    }

    /**
     * Whether the photograph behind [version] is one this handset still has to fetch.
     *
     * No stored bytes, or a `v` that names a different photograph. A `null` version is *"the link
     * did not say"*, which is treated as a miss rather than a hit: fetching an avatar that was
     * already current costs one request, and drawing last month's costs the driver their face.
     */
    public fun needsPhoto(driverId: String, version: String?): Boolean {
        val cached = read(driverId)

        return cached.photoBytes == null || version == null || cached.photoVersion != version
    }

    /**
     * Forgets the driver entirely (D-26, PDPA).
     *
     * Called on sign-out: the next person to sign in on this handset must not see the last one's
     * name and face while their own profile loads.
     */
    public fun clear() {
        db.transaction { db.sql.driverProfileQueries.deleteAll() }
    }
}
