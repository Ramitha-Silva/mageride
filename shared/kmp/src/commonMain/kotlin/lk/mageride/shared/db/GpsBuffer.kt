package lk.mageride.shared.db

import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import kotlin.time.Duration
import kotlin.time.Duration.Companion.hours
import kotlin.time.Instant

// The offline GPS ring buffer — mobile_db_schema.md §1.5 + §4.3, R-17, ADD §7.5.3, US-15.1.
//
// The foreground service writes every sample here, publishes live on `veh/{id}/pos/live`, and on
// reconnect drains the backlog to `veh/{id}/pos/replay` IN SEQ ORDER. C017 owns the wire — the
// cadence, the CBOR codec, the 4:1 live preemption and the 20/s replay ceiling of
// `PositionReplayQueue`. C018 owns the part that has to survive a process kill: the rows and the
// counter.

/** Where a buffered fix is in its life (`gps_buffer.state`). */
public enum class GpsSampleState {
    /** Captured, not yet on the wire. */
    PENDING,

    /** Went out live on `pos/live`. The server has it; the row is reapable. */
    PUBLISHED,

    /** Handed to the replay drain, not yet confirmed. */
    REPLAY_PENDING,

    /** The broker confirmed the replay. Reaped first by [GpsBuffer.evict]. */
    ACKED,
    ;

    /** Whether the platform has this sample and the row is only taking up space. */
    public val isDelivered: Boolean get() = this == PUBLISHED || this == ACKED

    public companion object {
        /** The stored spelling, or `null` when the CHECK domain has moved under us. */
        public fun fromWire(wire: String): GpsSampleState? = entries.firstOrNull { it.name == wire }
    }
}

/**
 * One buffered GNSS fix.
 *
 * A row-shaped mirror of C012's [PositionSample] — the same fix, minus the fields the device does
 * not know at capture time (`receivedTs` is the platform's, `fleetId` is denormalised server-side)
 * and plus the local delivery [state]. [toSample] is the bridge to the MQTT publisher.
 *
 * @property seq Monotonic per [vehicleId] and **strictly increasing**. The replay dedupe key: the
 *   server keeps `veh:seq:{vehicleId}` and discards anything at or below it (R-17, T-05).
 */
public data class BufferedFix(
    val vehicleId: Ulid,
    val seq: Long,
    val lat: Double,
    val lng: Double,
    val sampleTs: Instant,
    val createdAt: Instant,
    val accuracyM: Double? = null,
    val speedMps: Double? = null,
    val headingDeg: Int? = null,
    val hdop: Double? = null,
    val satCount: Int? = null,
    val source: PositionSource = PositionSource.MOBILE,
    val state: GpsSampleState = GpsSampleState.PENDING,
) {
    /**
     * The fix as the canonical wire payload.
     *
     * @param mode The vehicle's operating mode at capture, when the caller knows it.
     * @param vehicleType Denormalised for consumers, when the caller knows it.
     * @param tripId The Mode A/B tracking session; absent for Mode C (R-01).
     */
    public fun toSample(
        mode: ServiceMode? = null,
        vehicleType: VehicleType? = null,
        tripId: Ulid? = null,
    ): PositionSample = PositionSample(
        vehicleId = vehicleId,
        sampleTs = sampleTs,
        seq = seq,
        lat = lat,
        lng = lng,
        speedMps = speedMps,
        headingDeg = headingDeg,
        accuracyM = accuracyM,
        hdop = hdop,
        satCount = satCount,
        source = source,
        mode = mode,
        vehicleType = vehicleType,
        tripId = tripId,
    )
}

/**
 * §4.3's ring-buffer bounds.
 *
 * Three limits, applied in this order, because they answer three different failure modes: a
 * delivered row is pure waste, an old row is worthless to a server that has moved on, and a huge
 * row count is a full disk on a cheap handset.
 *
 * @property maxAge §4.3's worked example — "cap PENDING/REPLAY backlog (e.g. last 6 h per
 *   vehicle)".
 * @property maxRows §4.3's "hard row cap to bound disk". 50 000 is D6' §4.4's tracker flash ring
 *   and C017's [lk.mageride.shared.mqtt.PositionReplayQueue.RING_CAPACITY] — one number for the
 *   in-memory ring and the durable one, so a handset and a tracker lose history at the same point.
 */
public data class GpsRetentionPolicy(val maxAge: Duration = 6.hours, val maxRows: Long = 50_000) {
    init {
        require(maxAge > Duration.ZERO) { "maxAge must be positive" }
        require(maxRows > 0) { "maxRows must be positive" }
    }
}

/** What one [GpsBuffer.evict] pass removed. */
public data class GpsEviction(val delivered: Long, val aged: Long, val overflow: Long) {
    /** Rows removed in total. */
    public val total: Long get() = delivered + aged + overflow
}

/** Reads and writes `gps_buffer`. One implementation per database; see [OutboxStore]. */
public interface GpsBufferStore {

    /** Inserts, ignoring a `(vehicle_id, seq)` that is already present. */
    public fun insert(fix: BufferedFix)

    /** The backlog for [vehicleId] in `seq` order — `PENDING` and `REPLAY_PENDING` only. */
    public fun replayBatch(vehicleId: Ulid, limit: Long): List<BufferedFix>

    /** Every row for [vehicleId] in `seq` order. Diagnostics and tests. */
    public fun all(vehicleId: Ulid): List<BufferedFix>

    /** Every vehicle holding rows — the retention sweep bounds the ring per vehicle. */
    public fun vehicles(): List<Ulid>

    /** The highest `seq` any row for [vehicleId] carries, or `null` when there are none. */
    public fun highestSeq(vehicleId: Ulid): Long?

    /** How many rows [vehicleId] has. */
    public fun count(vehicleId: Ulid): Long

    /** Moves one row to [state]. */
    public fun setState(vehicleId: Ulid, seq: Long, state: GpsSampleState)

    /** Marks everything at or below [seq] as `ACKED`. */
    public fun ackThrough(vehicleId: Ulid, seq: Long)

    /** `REPLAY_PENDING` back to `PENDING` — the drain was interrupted. */
    public fun resetInFlight(vehicleId: Ulid)

    /** Deletes delivered rows (`PUBLISHED` and `ACKED`). */
    public fun deleteDelivered(vehicleId: Ulid)

    /** Deletes rows captured before [cutoff]. */
    public fun deleteOlderThan(vehicleId: Ulid, cutoff: Instant)

    /** Deletes the [count] oldest rows **by `seq`**, so the survivors stay a contiguous run. */
    public fun deleteOldest(vehicleId: Ulid, count: Long)

    /** Drops every row for [vehicleId]. */
    public fun deleteVehicle(vehicleId: Ulid)

    /** Drops every row. */
    public fun deleteAll()

    /** Runs [body] in one database transaction. */
    public fun <T> transaction(body: () -> T): T
}

/**
 * Hands out `seq` values that survive a process kill.
 *
 * **If the counter rewinds, the vehicle goes silently dark.** `position-processor-svc` keeps
 * `veh:seq:{vehicleId}` and discards `seq <= last_seen_seq`, so every sample published after a
 * rewind is dropped while the app believes it is publishing. C017's in-memory
 * [lk.mageride.shared.mqtt.PositionSequencer] says "C018 persists [last]; construct this with the
 * stored value" — this is that.
 *
 * **Reserved in blocks, not one at a time.** Persisting on every fix would mean a write per GNSS
 * sample — at AL-12's 1 s near-geofence burst that is a database write per second for the whole
 * approach. Instead a block of [blockSize] values is reserved and persisted up front, and a crash
 * inside a block simply skips the unused tail. `seq` has to be *strictly increasing*, not gapless:
 * the server's watermark is a floor, and the `ux_positions_vehicle_seq` unique index only ever
 * rejects duplicates. A skipped range costs nothing; a reused one costs the shift.
 *
 * The starting point is `max(reserved watermark, highest seq still in the buffer)`, so a database
 * restored from a backup that is ahead of the watermark cannot rewind either.
 *
 * Not thread-safe: it belongs to the one foreground service that owns the position stream.
 *
 * @param meta Where the watermark lives (`meta['gps.seq.{vehicleId}']`).
 * @param vehicleId The vehicle this counter belongs to. One instance per vehicle.
 * @param floor The highest `seq` already on disk for this vehicle, if any.
 * @param blockSize How many values are reserved per persist.
 */
public class PersistentPositionSequencer(
    private val meta: MetaStore,
    private val vehicleId: Ulid,
    floor: Long = 0,
    private val blockSize: Long = DEFAULT_BLOCK_SIZE,
) {
    private val key = MetaKeys.gpsSeq(vehicleId)
    private var reserved: Long = maxOf(meta.getLong(key) ?: 0L, floor)
    private var current: Long = reserved

    init {
        require(blockSize > 0) { "blockSize must be positive" }
    }

    /** The last value handed out. */
    public val last: Long get() = current

    /** The high-water mark on disk — always at or above [last]. */
    public val watermark: Long get() = reserved

    /** The next `seq`. Strictly greater than [last], always. */
    public fun next(now: Instant): Long {
        current += 1
        if (current > reserved) {
            reserved = current + blockSize
            meta.putLong(key, reserved, now)
        }
        return current
    }

    /**
     * Raises the counter to at least [seq], persisting if that moves the reservation.
     *
     * For a sample that reached the wire without going through [next] — a replay read back off
     * disk before the counter was restored, say. Never lowers it.
     */
    public fun observe(seq: Long, now: Instant) {
        if (seq <= current) return
        current = seq
        if (current > reserved) {
            reserved = current + blockSize
            meta.putLong(key, reserved, now)
        }
    }

    public companion object {
        /** Values reserved per persist. 100 at a 1 s cadence is one write every 100 s. */
        public const val DEFAULT_BLOCK_SIZE: Long = 100
    }
}

/**
 * The durable half of the position pipeline: the rows, the counter and the eviction rules.
 *
 * Pairs with C017's [lk.mageride.shared.mqtt.PositionReplayQueue], which owns pacing (the 2 s
 * post-reconnect live drain, the 4:1 live preemption, the 20/s ceiling) and holds nothing across
 * a restart. The split is deliberate: pacing is a property of the current connection, the backlog
 * is a property of the device.
 *
 * Blocking. One instance per vehicle; construct it through [MageRideDb.gpsBuffer].
 *
 * @param store The table.
 * @param sequencer This vehicle's persistent counter.
 * @param vehicleId The vehicle.
 * @param policy Ring bounds.
 */
public class GpsBuffer(
    private val store: GpsBufferStore,
    private val sequencer: PersistentPositionSequencer,
    public val vehicleId: Ulid,
    public val policy: GpsRetentionPolicy = GpsRetentionPolicy(),
) {

    /** The last `seq` handed out for this vehicle. */
    public val lastSeq: Long get() = sequencer.last

    /**
     * Buffers a fix, allocating its `seq`.
     *
     * The row is written before anything is published, which is the whole point: a process killed
     * between capture and publish still replays the sample.
     */
    @Suppress("LongParameterList") // One parameter per GNSS field the schema stores; a holder type
    // would just be BufferedFix without its seq.
    public fun record(
        lat: Double,
        lng: Double,
        sampleTs: Instant,
        now: Instant,
        accuracyM: Double? = null,
        speedMps: Double? = null,
        headingDeg: Int? = null,
        hdop: Double? = null,
        satCount: Int? = null,
        source: PositionSource = PositionSource.MOBILE,
    ): BufferedFix = store.transaction {
        val fix = BufferedFix(
            vehicleId = vehicleId,
            seq = sequencer.next(now),
            lat = lat,
            lng = lng,
            sampleTs = sampleTs,
            createdAt = now,
            accuracyM = accuracyM,
            speedMps = speedMps,
            headingDeg = headingDeg,
            hdop = hdop,
            satCount = satCount,
            source = source,
        )
        store.insert(fix)
        fix
    }

    /**
     * Buffers a fix that already carries a `seq` — a sample C017 built directly, or one being
     * re-admitted.
     *
     * The counter is raised to match so the next [record] cannot collide with it. A `seq` at or
     * below one already stored is ignored: `INSERT OR IGNORE` on `(vehicle_id, seq)` is the same
     * local dedupe rule C017's in-memory ring applies (`BufferOutcome.DUPLICATE`).
     */
    public fun record(sample: PositionSample, now: Instant): BufferedFix = store.transaction {
        require(sample.vehicleId == vehicleId) { "sample belongs to ${sample.vehicleId}, buffer to $vehicleId" }
        sequencer.observe(sample.seq, now)
        val fix = BufferedFix(
            vehicleId = sample.vehicleId,
            seq = sample.seq,
            lat = sample.lat,
            lng = sample.lng,
            sampleTs = sample.sampleTs,
            createdAt = now,
            accuracyM = sample.accuracyM,
            speedMps = sample.speedMps,
            headingDeg = sample.headingDeg,
            hdop = sample.hdop,
            satCount = sample.satCount,
            source = sample.source,
        )
        store.insert(fix)
        fix
    }

    /** The sample went out live; the platform has it. */
    public fun onPublishedLive(seq: Long) {
        store.setState(vehicleId, seq, GpsSampleState.PUBLISHED)
    }

    /**
     * The next slice of the backlog, **in `seq` order**.
     *
     * Ordering is not a nicety: the server's watermark only moves forward, so a batch delivered
     * out of order loses everything behind the highest `seq` in it.
     */
    public fun replayBatch(limit: Int = DEFAULT_REPLAY_BATCH): List<BufferedFix> =
        store.replayBatch(vehicleId, limit.toLong())

    /** [replayBatch] has been handed to the publisher. */
    public fun onReplayStarted(seqs: Iterable<Long>) {
        store.transaction { seqs.forEach { store.setState(vehicleId, it, GpsSampleState.REPLAY_PENDING) } }
    }

    /** The broker confirmed everything up to and including [seq]. */
    public fun onReplayAcked(seq: Long) {
        store.ackThrough(vehicleId, seq)
    }

    /** The link dropped mid-drain: un-confirmed rows go back in the queue. */
    public fun onReplayInterrupted() {
        store.resetInFlight(vehicleId)
    }

    /** How many rows this vehicle is holding. */
    public fun size(): Long = store.count(vehicleId)

    /** The backlog as it stands, in `seq` order. Diagnostics and tests. */
    public fun snapshot(): List<BufferedFix> = store.all(vehicleId)

    /**
     * Applies §4.3's three ring-buffer rules, in order, and reports what went.
     *
     * 1. **Delivered rows go.** `ACKED` (replay-confirmed) and `PUBLISHED` (went out live) are
     *    both already on the platform.
     * 2. **Age cap.** Anything captured before `now - maxAge`. The server would discard a
     *    six-hour-old fix as implausible anyway (§12.6), so holding it only costs disk.
     * 3. **Hard row cap**, oldest first **by `seq`**.
     *
     * Every rule deletes a PREFIX of the ascending run, never a hole in the middle — after any
     * eviction the surviving backlog is still contiguous and ascending, which is what keeps the
     * server's per-vehicle watermark advancing over it cleanly.
     */
    public fun evict(now: Instant, bounds: GpsRetentionPolicy = policy): GpsEviction = store.transaction {
        val start = store.count(vehicleId)
        store.deleteDelivered(vehicleId)
        val afterDelivered = store.count(vehicleId)

        store.deleteOlderThan(vehicleId, now - bounds.maxAge)
        val afterAge = store.count(vehicleId)

        val overflow = (afterAge - bounds.maxRows).coerceAtLeast(0)
        if (overflow > 0) store.deleteOldest(vehicleId, overflow)

        GpsEviction(
            delivered = start - afterDelivered,
            aged = afterDelivered - afterAge,
            overflow = overflow,
        )
    }

    /** Drops the whole backlog — the session ended, or the driver switched vehicle. */
    public fun clear() {
        store.deleteVehicle(vehicleId)
    }

    public companion object {
        /** Rows per replay slice. Sized under the 20/s ceiling so one slice is about a second. */
        public const val DEFAULT_REPLAY_BATCH: Int = 20
    }
}
