package lk.mageride.shared.mqtt

import kotlinx.cinterop.BetaInteropApi
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.convert
import kotlinx.cinterop.usePinned
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.db.GpsBuffer
import platform.Foundation.NSData
import platform.Foundation.create
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

/**
 * The MQTT socket, as the pipeline needs it — a byte pipe and nothing else.
 *
 * **The Swift side implements this and knows nothing about the position plane.** Topics come from
 * [MqttTopics], the QoS and retain flags from [MqttTopicKind], the payload from [PositionCodec] and
 * the credential from [MqttConfig]; a CocoaMQTT wrapper that also spelled a topic would be the
 * second copy of a contract that already has one — which is the same rule
 * `apps/driver-android/.../location/MqttPositionTransport.kt` states for the HiveMQ half.
 *
 * A Kotlin interface exports as an Objective-C protocol, so `CocoaMqttTransport: IosPositionTransport`
 * in Swift is all the binding this needs.
 */
public interface IosPositionTransport {

    /** Whether the broker connection is up right now. */
    public val isConnected: Boolean

    /**
     * Publishes [payload] on [topic].
     *
     * @param qos 0/1/2, from [MqttTopicKind.qos].
     * @param retain From [MqttTopicKind.retain].
     * @return whether the client accepted it for delivery. `false` puts the sample back on the
     *   backlog rather than losing it.
     */
    public fun publish(topic: String, payload: NSData, qos: Int, retain: Boolean): Boolean
}

/**
 * Where a sample went. Mirrors `FixOutcome` on the Android side.
 *
 * A sealed class rather than an enum because two of the three carry the `seq` the caller needs for
 * its own logging; Objective-C sees three concrete classes, which Swift switches on with `is`.
 */
public sealed class IosFixOutcome {

    /** Published on `veh/{vehicleId}/pos/live`. */
    public data class Published(public val seq: Long) : IosFixOutcome()

    /** Recorded and waiting for replay (R-17). */
    public data class Buffered(public val seq: Long) : IosFixOutcome()

    /** Neither — the cadence, the coalesce rule or the broker ceiling said no. */
    public data class Skipped(public val reason: String) : IosFixOutcome()
}

/**
 * Everything between a GNSS fix and the wire, with no CoreLocation and no broker in it.
 *
 * **The iOS counterpart of `apps/driver-android/.../location/PositionPipeline.kt`, and it is Kotlin
 * on purpose.** The Android app can hold this in its own module because Kotlin-to-Kotlin has no
 * boundary; on iOS every collaborator — [AdaptiveRateEngine], [PositionReplayQueue], [GpsBuffer],
 * [PositionSample], [Timestamp] — is on the Kotlin side of one, and several of those types cross it
 * lossily (`kotlin.time.Duration` is an inline value class the Objective-C export flattens to an
 * opaque `Long`; a nullable `Int` becomes a boxed `KotlinInt?`; `data class copy` becomes a
 * fifteen-argument `doCopy`). A Swift port would marshal five of them on every GPS tick, and the
 * two behaviours this component's DoD names — R-17's replay and D5' §5.2's cadence — are exactly
 * what a mis-typed marshalling breaks silently. So the seam is one level out: **Swift owns the fix
 * source and the socket, Kotlin owns the rules**, and the file is type-checked by
 * `:shared:compileKotlinIosArm64` on the Linux build host rather than only on a Mac.
 *
 * Three rules, none of them invented here:
 *
 * - **cadence** is [AdaptiveRateEngine] — D5' §5.2's phase table plus any server `setPosRate` hint
 *   (R-07). This class never picks an interval; it asks.
 * - **durability and `seq`** are [GpsBuffer] over C018's `gps_buffer`. The row is written **before**
 *   anything reaches the wire, so a fix captured a moment before the process dies is still replayed
 *   when it comes back — and the `seq` behind it is `PersistentPositionSequencer`'s, which reserves
 *   blocks on disk. If `seq` ever rewound, `position-processor-svc` would discard everything
 *   published afterwards and the vehicle would go silently dark while the app believed it was
 *   publishing (R-17, T-05).
 * - **replay pacing** is [PositionReplayQueue] — the 2 s post-reconnect live-drain window, the 4:1
 *   live preemption and the 20 msg/s ceiling (R-09, D-17).
 *
 * The two backlogs are one backlog: the **table** is the record and the **queue is the pacing window
 * over it**, refilled from disk by [refillFromJournal]. Memory alone loses it on a process kill;
 * disk alone drops every rate rule R-09 asks for.
 *
 * What is genuinely this class's own is the *order*: record, ask, publish or hold, then tell the
 * engine and the queue what happened. Getting that order wrong is how a fix reaches the wire without
 * a durable row behind it, or a retry escapes the broker ceiling.
 *
 * Not thread-safe — one instance, on the app's serial position queue.
 *
 * @param buffer C018's ring for this vehicle. `DriverDb.gpsBuffer(vehicleId)`.
 * @param transport The socket. Supplied by Swift.
 * @param mode The operating mode stamped onto every sample, when there is one.
 * @param vehicleType Likewise the type — MAP-03 colours the marker from it.
 */
@OptIn(ExperimentalTime::class)
public class IosPositionPipeline(
    private val buffer: GpsBuffer,
    private val transport: IosPositionTransport,
    private val mode: ServiceMode?,
    private val vehicleType: VehicleType?,
) {

    /**
     * The clock, as a seam an `iosTest` can wind.
     *
     * A mutable `internal` property rather than a constructor parameter, and both halves matter:
     * `internal` keeps it out of the generated Objective-C header, so the Swift initialiser stays
     * four arguments; a constructor parameter with a default would not have helped, because Kotlin
     * default arguments do not survive the export either — Swift would have had to pass a closure
     * returning a type it cannot name.
     */
    internal var clock: () -> Timestamp = { Clock.System.now() }

    private val engine = AdaptiveRateEngine()
    private val queue = PositionReplayQueue()
    private var lastPublished: PublishedFix? = null
    private var tripId: Ulid? = null

    /** How many samples are waiting to be replayed. C088's dashboard shows this. */
    public val bufferedCount: Long get() = buffer.size()

    /**
     * The cadence in force, in **seconds**.
     *
     * Seconds rather than a `Duration` because `Duration` does not survive the Objective-C export
     * intact. Nothing on iOS re-registers the location manager with it — `CLLocationManager` has no
     * time interval and delivers on distance and on its own schedule, so the cadence is enforced
     * entirely by [onFix] rejecting a fix that arrived too soon. That is the one real difference
     * between this pipeline and the Android one, and it lives in the fix *source*, not in the rule.
     */
    public fun intervalSeconds(): Double = engine.interval(clock()).inWholeMilliseconds / MILLIS_PER_SECOND

    /** The workflow phase changed (ride accepted, geofence entered, session ended). */
    public fun onPhase(phase: GpsPhase) {
        engine.onPhase(phase)
    }

    /** A Mode A/B tracking session started or ended; Mode C rides carry no `tripId` (R-01). */
    public fun onTrip(tripId: Ulid?) {
        this.tripId = tripId
    }

    /**
     * A downlink command arrived on `veh/{vehicleId}/cmd`.
     *
     * Takes the raw bytes so Swift never decodes a payload: [MqttCommands] owns the envelope, its
     * expiry and the unknown-command case. Only `setPosRate` changes anything here (R-07).
     *
     * @return whether the cadence changed, so the caller can log it.
     */
    public fun onCommand(payload: NSData): Boolean {
        val now = clock()
        val delivery = MqttCommands.decode(payload.toByteArray(), now)
        val command = (delivery as? CommandDelivery.Deliver)?.command
        if (command !is MqttCommand.SetPosRate) return false
        engine.onCadenceHint(command, now)
        return true
    }

    /**
     * The socket came back.
     *
     * Opens the 2 s live-drain window so the vehicle's *current* position reaches the platform
     * before its history does — a returning city's worth of vehicles replaying at once is the
     * reconnect storm R-09 exists to stop — and reloads the pacing ring from disk, because the
     * backlog may have been written by a process that is no longer running.
     */
    public fun onReconnected() {
        val now = clock()
        buffer.onReplayInterrupted()
        queue.onReconnected(now)
        refillFromJournal()
    }

    /**
     * Offers one fix.
     *
     * A fix the cadence rejects never reaches the buffer, so it consumes no `seq`: `seq` numbers
     * what was captured for the wire, and burning one per rejected tick would run the server's
     * watermark ahead of the samples it exists to order.
     *
     * Nullable fields are boxed on the Swift side (`KotlinDouble?`, `KotlinInt?`) rather than
     * given sentinel values, because CoreLocation's own sentinels are negative numbers and a
     * `-1` accuracy that reached `telemetry.positions` would be a metre reading.
     *
     * @param sampleTsEpochSeconds `CLLocation.timestamp.timeIntervalSince1970` — the instant the
     *   fix was *taken*, which is not the instant it was delivered.
     */
    // LongParameterList: one parameter per CLLocation field the platform carries.
    // ReturnCount: three returns, one per outcome in [IosFixOutcome]. A single exit would need a
    // mutable result built up across the branches, which is strictly harder to read than the three
    // names — the same call the Android pipeline makes.
    @Suppress("LongParameterList", "ReturnCount")
    public fun onFix(
        lat: Double,
        lng: Double,
        sampleTsEpochSeconds: Double,
        accuracyM: Double? = null,
        speedMps: Double? = null,
        headingDeg: Int? = null,
        satCount: Int? = null,
    ): IosFixOutcome {
        val now = clock()
        val decision = engine.decide(now, GeoPoint(lat = lat, lng = lng), lastPublished)
        if (decision is PublishDecision.Skip) return IosFixOutcome.Skipped(decision.reason.name)

        val sample = buffer.record(
            lat = lat,
            lng = lng,
            sampleTs = Timestamp.fromEpochMilliseconds((sampleTsEpochSeconds * MILLIS_PER_SECOND).toLong()),
            now = now,
            accuracyM = accuracyM,
            speedMps = speedMps,
            headingDeg = headingDeg,
            satCount = satCount,
            // MOBILE, always: this stream is a handset. A tracker's samples arrive through C043
            // with their own source, and `telemetry.positions.source` is what tells the two apart.
            source = PositionSource.MOBILE,
        ).toSample(mode = mode, vehicleType = vehicleType, tripId = tripId)

        if (!transport.isConnected || !publish(sample, MqttTopicKind.POSITION_LIVE)) {
            // Offline, or the socket died between the check and the write. The row is already on
            // disk with its seq; the ring gets a copy so the pacing rules apply when it drains.
            queue.buffer(sample)
            return IosFixOutcome.Buffered(sample.seq)
        }

        buffer.onPublishedLive(sample.seq)
        // Counts a retry too — the broker's 5 msg/s ceiling counts every message that crosses the
        // wire, not every distinct fix.
        engine.onPublished(now)
        queue.onLivePublished()
        lastPublished = PublishedFix(point = GeoPoint(lat = lat, lng = lng), at = now)
        return IosFixOutcome.Published(sample.seq)
    }

    /**
     * Drains as much backlog as the pacing rules allow right now.
     *
     * Called on a timer while the socket is up, not once after a reconnect: the drain window, the
     * 4:1 fair share and the 20/s ceiling all mean the backlog leaves in slices, so a single pass
     * would publish a handful of samples and stop with hours of history still on disk.
     *
     * @return how many backlog samples reached the broker.
     */
    public fun drainReplay(livePending: Boolean = false): Int {
        if (!transport.isConnected) return 0
        val now = clock()
        if (queue.isEmpty) refillFromJournal()

        var sent = 0
        var lastAcked: Long? = null
        var next = queue.peek(now, livePending)
        while (next != null) {
            if (!publish(next, MqttTopicKind.POSITION_REPLAY)) {
                // Un-confirmed rows go back to PENDING so the next reconnect re-reads them; the
                // sample stays at the head of the ring, so nothing is lost either way.
                buffer.onReplayInterrupted()
                break
            }
            queue.onPublished(now)
            lastAcked = next.seq
            sent++
            next = queue.peek(now, livePending)
        }
        // One ack per drain rather than per sample: `ackThrough` is a range update and `seq` is
        // ordered, so the highest one confirms everything below it in a single statement.
        lastAcked?.let(buffer::onReplayAcked)
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
        val batch = buffer.replayBatch(REPLAY_BATCH)
        if (batch.isEmpty()) return
        batch.map { it.toSample(mode = mode, vehicleType = vehicleType, tripId = tripId) }
            .forEach(queue::buffer)
        buffer.onReplayStarted(batch.map { it.seq })
    }

    private fun publish(sample: PositionSample, kind: MqttTopicKind): Boolean {
        val topic = when (kind) {
            MqttTopicKind.POSITION_REPLAY -> MqttTopics.positionReplay(sample.vehicleId)
            else -> MqttTopics.positionLive(sample.vehicleId)
        }
        return transport.publish(
            topic = topic,
            payload = PositionCodec.encode(sample).toNSData(),
            qos = kind.qos.qosLevel,
            retain = kind.retain,
        )
    }

    private companion object {
        /** `GpsBuffer.DEFAULT_REPLAY_BATCH` — one slice sized to about a second under the 20/s ceiling. */
        const val REPLAY_BATCH = 20
        const val MILLIS_PER_SECOND = 1000.0
    }
}

@OptIn(ExperimentalForeignApi::class, BetaInteropApi::class)
internal fun ByteArray.toNSData(): NSData {
    if (isEmpty()) return NSData()
    return usePinned { pinned -> NSData.create(bytes = pinned.addressOf(0), length = size.convert()) }
}

@OptIn(ExperimentalForeignApi::class)
internal fun NSData.toByteArray(): ByteArray {
    val size = length.toInt()
    if (size == 0) return ByteArray(0)
    val out = ByteArray(size)
    out.usePinned { pinned -> platform.posix.memcpy(pinned.addressOf(0), bytes, length) }
    return out
}
