package lk.mageride.shared.mqtt

import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

// The offline GPS buffer and its replay (R-17, T-05, ADD §7.5.3 / §11.13, D5' §5.3).
//
// A driver loses coverage inside a ride. The foreground service keeps sampling into a local ring
// buffer, and when the link comes back the backlog is published to `veh/{vehicleId}/pos/replay` —
// a SEPARATE TOPIC from `pos/live`, because a whole city's vehicles replaying at once is exactly
// the reconnect storm R-09 exists to stop, and a backlog must never delay a current position.
//
// `seq` IS THE DEDUPE KEY, AND IT IS STRICTLY MONOTONIC PER VEHICLE. `position-processor-svc`
// keeps `veh:seq:{vehicleId}` and discards `seq <= last_seen_seq`; the hypertable then rejects an
// exact duplicate through `ux_positions_vehicle_seq (vehicle_id, seq, sample_ts)`. Both of those
// are server-side safety nets — this queue drops duplicates locally so the radio never carries
// them in the first place.

/**
 * Hands out the strictly monotonic `seq` every published sample carries.
 *
 * **The counter must survive a process restart.** If it rewinds, every sample the app publishes
 * after the restart carries a `seq` the server has already seen, and `position-processor-svc`
 * discards all of them — the vehicle goes silently dark while the app believes it is publishing.
 * C018 persists [last] to the local database; construct this with the stored value.
 *
 * Not thread-safe: it belongs to the one foreground service that owns the position stream.
 *
 * @param start The highest `seq` already used, from local storage. `0` on a fresh install.
 */
public class PositionSequencer(start: Long = 0) {

    private var current: Long = start

    /** The last value handed out — persist this. */
    public val last: Long get() = current

    /** The next `seq`. Strictly greater than [last], always. */
    public fun next(): Long = ++current

    /**
     * Raises the counter to at least [seq].
     *
     * For the case where a sample reaches the wire without going through [next] — a restart that
     * read a buffered sample back before the counter was restored, say. Never lowers it.
     */
    public fun observe(seq: Long) {
        if (seq > current) current = seq
    }
}

/** What happened to a sample offered to the buffer. */
public enum class BufferOutcome {
    /** Stored, awaiting replay. */
    BUFFERED,

    /** `seq` was not greater than the last one buffered — a duplicate, dropped locally (R-17). */
    DUPLICATE,

    /** Stored, and the oldest sample was evicted to make room (the ring is full). */
    BUFFERED_WITH_EVICTION,
}

/**
 * The device's offline sample ring, and the rules for draining it.
 *
 * Three rules, all from the spec:
 *
 * 1. **Strictly monotonic `seq`, deduped locally.** A sample whose `seq` is not greater than the
 *    last buffered one is [BufferOutcome.DUPLICATE] and never reaches the wire.
 * 2. **Live drains first for 2 s after a reconnect.** A returning vehicle's *current* position is
 *    worth more than its history, and the drain window keeps the backlog from arriving before it
 *    (R-09, `mqtt-topics.md` §5).
 * 3. **Live preempts replay 4:1, and no more than 20 samples/s.** The ratio is weighted fair
 *    share, not a hard gate: while live traffic is flowing, one replay is admitted per four live
 *    publishes; when live is idle the backlog drains at the full
 *    [MqttRateLimits.REPLAY_MSG_PER_SECOND] ceiling rather than stalling behind a stream that is
 *    not there.
 *
 * The queue never publishes anything itself — [peek] offers the head, and the caller confirms with
 * [onPublished] once the broker has taken it. A publish that fails simply is not confirmed, so the
 * head stays put and no in-flight bookkeeping is needed.
 *
 * Not thread-safe; one instance per vehicle, on the position stream's own coroutine.
 *
 * @param capacity Ring size. The tracker figure from D6' §4.4 (50,000 samples ≈ 14 hours at a 1 s
 *   cadence) is the default; a handset with less storage can pass less.
 * @param maxPerSecond Replay ceiling — 20/s/device (D-17).
 * @param liveDrain How long after a reconnect replay stays locked.
 * @param livePreemptRatio Live publishes admitted per replay publish while both are flowing.
 */
public class PositionReplayQueue(
    private val capacity: Int = RING_CAPACITY,
    private val maxPerSecond: Int = MqttRateLimits.REPLAY_MSG_PER_SECOND,
    private val liveDrain: Duration = LIVE_DRAIN_WINDOW,
    private val livePreemptRatio: Int = LIVE_PREEMPT_RATIO,
) {
    private val ring = ArrayDeque<PositionSample>()
    private val publishedAt = ArrayDeque<Timestamp>()

    private var lastBufferedSeq: Long = Long.MIN_VALUE
    private var replayUnlockAt: Timestamp? = null
    private var liveSinceLastReplay: Int = 0

    /** How many samples are waiting. */
    public val size: Int get() = ring.size

    /** Whether anything is waiting to be replayed. */
    public val isEmpty: Boolean get() = ring.isEmpty()

    /** The highest `seq` accepted into the buffer. */
    public val highestSeq: Long get() = lastBufferedSeq

    /**
     * Offers a sample captured while offline.
     *
     * @return whether it was stored, and whether storing it cost the oldest entry.
     */
    public fun buffer(sample: PositionSample): BufferOutcome {
        if (sample.seq <= lastBufferedSeq) return BufferOutcome.DUPLICATE
        lastBufferedSeq = sample.seq

        val evicted = if (ring.size >= capacity) {
            ring.removeFirst()
            true
        } else {
            false
        }
        ring.addLast(sample)
        return if (evicted) BufferOutcome.BUFFERED_WITH_EVICTION else BufferOutcome.BUFFERED
    }

    /**
     * The socket just came back: hold replay for [liveDrain] so live samples go first.
     *
     * @param now When the connection was established.
     */
    public fun onReconnected(now: Timestamp) {
        replayUnlockAt = now + liveDrain
        liveSinceLastReplay = 0
        publishedAt.clear()
    }

    /** Tell the queue a live sample went out — this is what earns replay its next slot. */
    public fun onLivePublished() {
        liveSinceLastReplay++
    }

    /**
     * The next backlog sample to publish, or `null` if replay may not proceed right now.
     *
     * @param now Wall clock.
     * @param livePending Whether a live sample is also waiting to go out. When it is, the 4:1
     *   fair share applies; when it is not, only the 20/s ceiling and the drain window do.
     */
    @Suppress("ReturnCount") // One guard per rule in the class KDoc; nesting them reads worse.
    public fun peek(now: Timestamp, livePending: Boolean = false): PositionSample? {
        if (ring.isEmpty()) return null
        if (isDraining(now)) return null
        if (livePending && liveSinceLastReplay < livePreemptRatio) return null
        if (!withinRateLimit(now)) return null
        return ring.first()
    }

    /**
     * Confirms that the sample [peek] returned reached the broker: it leaves the ring and counts
     * against the 20/s budget.
     *
     * @return the sample that was removed, or `null` if the ring was empty.
     */
    public fun onPublished(now: Timestamp): PositionSample? {
        val sample = ring.removeFirstOrNull() ?: return null
        publishedAt.addLast(now)
        liveSinceLastReplay = 0
        return sample
    }

    /** Whether replay is still inside the post-reconnect live-drain window. */
    public fun isDraining(now: Timestamp): Boolean = replayUnlockAt?.let { now < it } == true

    /** Empties the buffer — the backlog was published, or the session ended. */
    public fun clear() {
        ring.clear()
        publishedAt.clear()
    }

    private fun withinRateLimit(now: Timestamp): Boolean {
        while (publishedAt.isNotEmpty() && now - publishedAt.first() >= RATE_WINDOW) {
            publishedAt.removeFirst()
        }
        return publishedAt.size < maxPerSecond
    }

    public companion object {
        /** D6' §4.4's flash ring — 50,000 samples. */
        public const val RING_CAPACITY: Int = 50_000

        /** Live samples admitted per replay sample while both streams are flowing (D6' §3.5). */
        public const val LIVE_PREEMPT_RATIO: Int = 4

        /** How long live drains alone after a reconnect (R-09). */
        public val LIVE_DRAIN_WINDOW: Duration = 2.seconds

        /** The window the replay ceiling is measured over. */
        public val RATE_WINDOW: Duration = 1.seconds
    }
}
