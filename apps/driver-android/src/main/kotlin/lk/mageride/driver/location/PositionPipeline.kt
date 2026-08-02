package lk.mageride.driver.location

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.mqtt.AdaptiveRateEngine
import lk.mageride.shared.mqtt.GpsPhase
import lk.mageride.shared.mqtt.MqttCommand
import lk.mageride.shared.mqtt.PositionReplayQueue
import lk.mageride.shared.mqtt.PublishDecision
import lk.mageride.shared.mqtt.PublishedFix

/**
 * One GNSS fix as `FusedLocationProviderClient` hands it over, before it has a `seq`.
 *
 * A plain value rather than `android.location.Location` so the whole pipeline is testable on the
 * JVM — which is the point, because everything the DoD says about this component (cadence,
 * buffering, monotonic replay) is decided here and none of it needs a radio.
 */
internal data class Fix(
    val lat: Double,
    val lng: Double,
    val sampleTs: Timestamp,
    val accuracyM: Double? = null,
    val speedMps: Double? = null,
    val headingDeg: Int? = null,
    val satCount: Int? = null,
)

/** Where a sample went. */
internal sealed interface FixOutcome {

    /** Published on `veh/{vehicleId}/pos/live`. */
    data class Published(val sample: PositionSample) : FixOutcome

    /** Recorded and waiting for replay (R-17). */
    data class Buffered(val sample: PositionSample) : FixOutcome

    /** Neither — the cadence, the coalesce rule or the broker ceiling said no. */
    data class Skipped(val reason: String) : FixOutcome
}

/**
 * The durable side of the position stream: C018's `gps_buffer` table, behind an interface.
 *
 * **This is where `seq` comes from, and it is why.** The counter behind [record] is C018's
 * `PersistentPositionSequencer`, which reserves blocks on disk — so it survives a process kill.
 * If it ever rewound, `position-processor-svc` would discard every sample published afterwards
 * (it keeps `veh:seq:{vehicleId}` and drops anything at or below it) and the vehicle would go
 * silently dark while the app believed it was publishing (R-17, T-05).
 *
 * The row is written **before** anything reaches the wire, which is the whole point: a fix
 * captured a moment before the process dies is still replayed when it comes back.
 */
internal interface PositionJournal {

    /** Records [fix], allocating its durable `seq`. */
    fun record(fix: Fix, now: Timestamp): PositionSample

    /** [sample] went out on `pos/live`; the platform has it. */
    fun onPublishedLive(seq: Long)

    /** The next slice of the backlog, in `seq` order. `PENDING` and `REPLAY_PENDING` only. */
    fun replayBatch(limit: Int): List<PositionSample>

    /** The slice has been handed to the publisher. */
    fun onReplayStarted(seqs: List<Long>)

    /** The broker confirmed everything up to and including [seq]. */
    fun onReplayAcked(seq: Long)

    /** The link dropped mid-drain — un-confirmed rows go back in the queue. */
    fun onReplayInterrupted()

    /** How many rows are held. Drives the foreground notification. */
    fun size(): Long
}

/** What the pipeline publishes through. The service supplies the HiveMQ-backed implementation. */
internal interface PositionTransport {

    /** Whether the broker connection is up right now. */
    val isConnected: Boolean

    /** Publishes on `pos/live`. Returns whether the broker took it. */
    fun publishLive(sample: PositionSample): Boolean

    /** Publishes on `pos/replay` — a separate topic, deliberately (R-09). */
    fun publishReplay(sample: PositionSample): Boolean
}

/**
 * Everything between a GNSS fix and the wire, with no Android and no broker in it.
 *
 * Three rules, none of them invented here:
 *
 * - **cadence** is `:shared`'s [AdaptiveRateEngine] — D5' §5.2's phase table plus any server
 *   `setPosRate` hint (R-07). This class never picks an interval; it asks.
 * - **durability and `seq`** are [PositionJournal] over C018's `gps_buffer`.
 * - **replay pacing** is `:shared`'s [PositionReplayQueue] — the 2 s post-reconnect live-drain
 *   window, the 4:1 live preemption and the 20 msg/s ceiling (R-09, D-17).
 *
 * The two backlogs are one backlog: the **table** is the record, and the **queue is the pacing
 * window over it**, refilled from disk by [refillFromJournal]. Holding the samples only in memory
 * would lose them on a process kill; holding them only on disk would drop every rate rule R-09
 * asks for.
 *
 * What is genuinely this class's own is the *order*: record, ask, publish or hold, then tell the
 * engine and the queue what happened. Getting that order wrong is how a fix reaches the wire
 * without a durable row behind it, or a retry escapes the broker ceiling.
 *
 * Not thread-safe — one instance, on the foreground service's single position coroutine. There is
 * no `vehicleId` parameter because there is no way to get it wrong: the journal is C018's
 * per-vehicle `GpsBuffer` and the topic is built from the sample's own id, which EMQX then checks
 * against the credential's claim.
 */
internal class PositionPipeline(
    private val transport: PositionTransport,
    private val journal: PositionJournal,
    private val engine: AdaptiveRateEngine = AdaptiveRateEngine(),
    private val queue: PositionReplayQueue = PositionReplayQueue(),
    private val mode: ServiceMode? = null,
    private val vehicleType: VehicleType? = null,
) {
    private var lastPublished: PublishedFix? = null
    private var tripId: Ulid? = null

    /** How many samples are waiting to be replayed — the foreground notification shows this. */
    val bufferedCount: Long get() = journal.size()

    /** The cadence in force, for the caller to schedule its next location request with. */
    fun intervalAt(now: Timestamp) = engine.interval(now)

    /** The workflow phase changed (ride accepted, geofence entered, session ended). */
    fun onPhase(phase: GpsPhase) {
        engine.onPhase(phase)
    }

    /** A `setPosRate` arrived on `veh/{vehicleId}/cmd` (R-07). */
    fun onCadenceHint(hint: MqttCommand.SetPosRate, now: Timestamp) {
        engine.onCadenceHint(hint, now)
    }

    /** A Mode A/B tracking session started or ended; Mode C rides carry no `tripId` (R-01). */
    fun onTrip(id: Ulid?) {
        tripId = id
    }

    /**
     * The socket came back.
     *
     * Opens the 2 s live-drain window so the vehicle's *current* position reaches the platform
     * before its history does — a returning city's worth of vehicles replaying at once is the
     * reconnect storm R-09 exists to stop — and reloads the pacing ring from disk, because the
     * backlog may have been written by a process that is no longer running.
     */
    fun onReconnected(now: Timestamp) {
        journal.onReplayInterrupted()
        queue.onReconnected(now)
        refillFromJournal()
    }

    /**
     * Offers one fix.
     *
     * A fix the cadence rejects never reaches the journal, so it consumes no `seq`: `seq` numbers
     * what was captured for the wire, and burning one per rejected tick would run the counter
     * ahead of the samples it is there to order.
     */
    // Three returns, one per outcome in [FixOutcome]. A single exit would need a mutable result
    // built up across the branches, which is strictly harder to read than the three names.
    @Suppress("ReturnCount")
    fun onFix(now: Timestamp, fix: Fix): FixOutcome {
        val decision = engine.decide(now, fix.toPoint(), lastPublished)
        if (decision is PublishDecision.Skip) return FixOutcome.Skipped(decision.reason.name)

        val sample = journal.record(fix, now).withContext()

        if (!transport.isConnected || !transport.publishLive(sample)) {
            // Offline, or the socket died between the check and the write. The row is already on
            // disk with its seq; the ring gets a copy so the pacing rules apply when it drains.
            queue.buffer(sample)
            return FixOutcome.Buffered(sample)
        }

        journal.onPublishedLive(sample.seq)
        // Counts a retry too — the broker's 5 msg/s ceiling counts every message that crosses the
        // wire, not every distinct fix.
        engine.onPublished(now)
        queue.onLivePublished()
        lastPublished = PublishedFix(point = fix.toPoint(), at = now)
        return FixOutcome.Published(sample)
    }

    /**
     * Drains as much backlog as the pacing rules allow right now.
     *
     * Called on a timer while the socket is up, not once after a reconnect: the drain window, the
     * 4:1 fair share and the 20/s ceiling all mean the backlog leaves in slices, so a single pass
     * would publish a handful of samples and stop.
     *
     * @param livePending Whether a live sample is also waiting, which triggers the 4:1 preemption.
     * @return how many backlog samples reached the broker.
     */
    fun drainReplay(now: Timestamp, livePending: Boolean = false): Int {
        if (!transport.isConnected) return 0
        if (queue.isEmpty) refillFromJournal()

        var sent = 0
        var lastAcked: Long? = null
        var next = queue.peek(now, livePending)
        while (next != null) {
            if (!transport.publishReplay(next)) {
                // Un-confirmed rows go back to PENDING so the next reconnect re-reads them; the
                // sample stays at the head of the ring, so nothing is lost either way.
                journal.onReplayInterrupted()
                break
            }
            queue.onPublished(now)
            lastAcked = next.seq
            sent++
            next = queue.peek(now, livePending)
        }
        // One ack per drain rather than per sample: `ackThrough` is a range update, and `seq` is
        // ordered, so the highest one confirms everything below it in a single statement.
        lastAcked?.let(journal::onReplayAcked)
        return sent
    }

    /**
     * Tops the pacing ring up from the durable backlog.
     *
     * [PositionReplayQueue.buffer] drops anything whose `seq` is not above the highest it has
     * already seen, so re-offering a slice is idempotent and a partially-drained batch cannot be
     * re-sent.
     */
    private fun refillFromJournal() {
        val batch = journal.replayBatch(REPLAY_BATCH)
        if (batch.isEmpty()) return
        batch.forEach(queue::buffer)
        journal.onReplayStarted(batch.map(PositionSample::seq))
    }

    /** Stamps the fields the journal cannot know: the operating mode, the type and the trip. */
    private fun PositionSample.withContext(): PositionSample =
        copy(mode = mode, vehicleType = vehicleType, tripId = tripId)

    private companion object {
        /** `GpsBuffer.DEFAULT_REPLAY_BATCH` — one slice sized to about a second under the 20/s ceiling. */
        const val REPLAY_BATCH = 20
    }
}

/** A fix as the plain coordinate the cadence engine's distance check needs. */
private fun Fix.toPoint() = GeoPoint(lat = lat, lng = lng)
