package lk.mageride.shared.db

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.db.driver.DriverDb
import lk.mageride.shared.db.passenger.PassengerDb
import kotlin.time.Clock
import kotlin.time.Instant

/**
 * One open on-device database.
 *
 * The common surface is the machinery `:shared` itself implements — the durable command queue
 * (§1.4), the GPS ring (§1.5), the KV store (§1.12) and the retention sweep (§4.3). The
 * *projections* are not here on purpose: `rides` belongs to the passenger app and
 * `dispatch_offers` to the driver app, one consumer each, so they are reached through the typed
 * SQLDelight queries on [PassengerDb.sql] / [DriverDb.sql] rather than through a hand-written
 * abstraction that would exist only to be implemented twice.
 *
 * **Every call is blocking.** SQLDelight's Android and Native drivers are synchronous, and this
 * module deliberately does not pick a dispatcher for four apps: `Dispatchers.IO` is not resolvable
 * from `commonMain` for all our targets, and an `expect val` for it would be a platform seam that
 * buys nothing an app cannot express better. Run the drain worker, the sweep and any read that is
 * not a single row off the main thread.
 */
public interface MageRideDb : AutoCloseable {

    /** Which app this file belongs to (§0.2). */
    public val app: MageRideApp

    /** The durable command queue (§1.4, R-14, R-18). */
    public val outbox: CommandOutbox

    /** The key-value store (§1.12). */
    public val meta: MetaStore

    /** The §4.2 delta cursors, over [meta]. */
    public val cursors: SyncCursors

    /** The §4.3 sweep. */
    public val retention: Retention

    /**
     * The GPS ring for one vehicle (§1.5).
     *
     * Per vehicle because `seq` is: `gps_buffer`'s primary key is `(vehicle_id, seq)` and the
     * watermark key is [MetaKeys.gpsSeq]. Cached per id, so two callers for the same vehicle share
     * one counter — two [PersistentPositionSequencer]s over one vehicle would hand out the same
     * `seq` twice.
     *
     * Present on both databases because `gps_buffer` is a §1 shared table. The passenger app does
     * not run a position publisher (§1.5) and will never call this; Mode B private-vehicle sharing
     * is why the table is shared rather than driver-only.
     */
    public fun gpsBuffer(vehicleId: Ulid): GpsBuffer

    /** Runs [body] in one database transaction — the §4.1 "projection + outbox row" pair. */
    public fun <T> transaction(body: () -> T): T

    /**
     * Empties every table in place.
     *
     * The §0.4 wipe for logout, `403 device-revoked` (AL-08) and PDPA erasure is
     * [DatabaseDriverFactory.delete] — the whole file, plus [DatabaseKeyManager.forget] for the
     * key. This is the fallback for a caller that cannot close the connection first.
     */
    public fun wipe()

    /** Closes the connection. */
    override fun close()
}

/**
 * Opens the two databases.
 *
 * The single place a database is opened, which is also what makes [DatabaseKeyManager]'s
 * un-synchronised key mint safe: open once, at start-up, from here.
 *
 * @param drivers The platform's connection factory.
 * @param keys Where the SQLCipher key comes from (§0.4). **`null` opens the file unencrypted** —
 *   for tests and for a bring-up build, never for a shipping one. It is an explicit parameter
 *   rather than a silent fallback so that "we shipped without encryption" cannot happen by
 *   omission.
 * @param clock Stamps [MetaKeys.SCHEMA_REV] on open. Injectable, as everywhere else in this module.
 */
public class MageRideDatabaseFactory(
    private val drivers: DatabaseDriverFactory,
    private val keys: DatabaseKeyManager?,
    private val clock: () -> Instant = { Clock.System.now() },
) {

    /** Opens `mageride_passenger.db` — §1 + §2. */
    public suspend fun openPassenger(inMemory: Boolean = false): PassengerDb =
        PassengerDb(driverFor(MageRideApp.PASSENGER, inMemory)).stamp(PassengerDb.SCHEMA.version)

    /** Opens `mageride_driver.db` — §1 + §3. */
    public suspend fun openDriver(inMemory: Boolean = false): DriverDb =
        DriverDb(driverFor(MageRideApp.DRIVER, inMemory)).stamp(DriverDb.SCHEMA.version)

    /** Opens whichever database [app] names. */
    public suspend fun open(app: MageRideApp, inMemory: Boolean = false): MageRideDb = when (app) {
        MageRideApp.PASSENGER -> openPassenger(inMemory)
        MageRideApp.DRIVER -> openDriver(inMemory)
    }

    /**
     * The §0.4 erase: closes nothing, deletes the file, forgets the key.
     *
     * Close the [MageRideDb] first. Deleting the key as well as the file means a copy of the file
     * recovered from a backup is undecryptable, which is what E-06 asks for.
     */
    public suspend fun erase(app: MageRideApp): Boolean {
        val removed = drivers.delete(app)
        keys?.forget(app)
        return removed
    }

    /**
     * Records the app schema revision in `meta`, per §0.5 ("Room/SQLDelight track
     * `PRAGMA user_version`; a `meta` KV row **also** records the app schema rev").
     *
     * Written only when it changes, so a routine open costs a read rather than a write. The
     * authoritative version is still `user_version` — SQLDelight is what reads it and decides
     * whether to migrate; this row is the one a support bundle or a crash report can see.
     */
    private fun <T : MageRideDb> T.stamp(version: Long): T = apply {
        if (meta.getLong(MetaKeys.SCHEMA_REV) != version) meta.putLong(MetaKeys.SCHEMA_REV, version, clock())
    }

    private suspend fun driverFor(app: MageRideApp, inMemory: Boolean) = drivers.create(
        DatabaseRequest(
            app = app,
            schema = when (app) {
                MageRideApp.PASSENGER -> PassengerDb.SCHEMA
                MageRideApp.DRIVER -> DriverDb.SCHEMA
            },
            passphrase = if (inMemory) null else keys?.passphrase(app),
            inMemory = inMemory,
        ),
    )
}

/**
 * Shared plumbing for the two [MageRideDb] implementations.
 *
 * Holds the per-vehicle [GpsBuffer] cache and the [Retention]/[CommandOutbox] wiring that is
 * identical either way; the subclasses supply the stores their generated queries back.
 */
public abstract class BaseMageRideDb(override val app: MageRideApp) : MageRideDb {

    private val buffers = mutableMapOf<Ulid, GpsBuffer>()

    /** The store the per-vehicle buffers read and write. Module-internal: the two subclasses. */
    internal abstract val gpsStore: GpsBufferStore

    override val cursors: SyncCursors by lazy { SyncCursors(meta) }

    final override fun gpsBuffer(vehicleId: Ulid): GpsBuffer = buffers.getOrPut(vehicleId) {
        // The floor is the highest seq still on disk, so a database restored from a backup that is
        // ahead of the persisted watermark still cannot rewind (see PersistentPositionSequencer).
        val sequencer = PersistentPositionSequencer(
            meta = meta,
            vehicleId = vehicleId,
            floor = gpsStore.highestSeq(vehicleId) ?: 0L,
        )
        GpsBuffer(store = gpsStore, sequencer = sequencer, vehicleId = vehicleId)
    }

    /** Drops the cached buffer for [vehicleId] — the driver switched vehicle. */
    public fun releaseGpsBuffer(vehicleId: Ulid) {
        buffers.remove(vehicleId)
    }

    /**
     * Applies the §4.3 ring rules to every vehicle that still holds rows.
     *
     * Per vehicle because the row cap and the `seq` ordering are: a driver who changed vehicle
     * mid-shift leaves a backlog behind under the old id, and one global cap would evict the
     * active vehicle's samples to make room for the dead one's.
     */
    protected fun sweepGpsBuffers(now: Instant, bounds: GpsRetentionPolicy): Long =
        gpsStore.vehicles().sumOf { gpsBuffer(it).evict(now, bounds).total }

    /** Clears the buffer cache. Called by [wipe] and [close]. */
    protected fun forgetGpsBuffers() {
        buffers.clear()
    }
}

/** Convenience for the §4.3 sweep with the default policy. */
public fun MageRideDb.sweep(now: Instant): RetentionReport = retention.sweep(now)
