package lk.mageride.driver.location

import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.mqtt.AdaptiveRateEngine
import lk.mageride.shared.mqtt.GpsPhase
import lk.mageride.shared.mqtt.PositionReplayQueue
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * The three DoD lines the position stream owns, asserted without a radio or a broker.
 *
 * - *"the foreground service … keeps publishing at the phase-appropriate cadence"*
 * - *"a buffered GPS backlog replays on `veh/{vehicleId}/pos/replay` with monotonic seq after
 *   reconnect"* (R-17)
 * - and the rule underneath both: a sample never reaches the wire without a durable row and a
 *   `seq` behind it.
 *
 * Everything is driven off an injected `now`, so a two-hour outage is four lines rather than a
 * sleeping test.
 */
@OptIn(ExperimentalTime::class)
class PositionPipelineTest {

    private val vehicle: Ulid = "01J0VEHICLE0000000000000000"
    private val start: Timestamp = Timestamp.fromEpochMilliseconds(1_780_000_000_000)

    private val transport = FakeTransport()
    private val journal = FakeJournal(vehicle)

    @Test
    fun a_fix_is_published_live_when_the_socket_is_up() {
        val pipeline = pipeline()
        transport.isConnected = true

        val outcome = pipeline.onFix(start, fix(6.9271, 79.8612))

        assertIs<FixOutcome.Published>(outcome)
        assertEquals(1, transport.live.size, "one live publish")
        assertEquals(0, transport.replay.size, "nothing on the replay topic")
        assertTrue(journal.published.contains(outcome.sample.seq), "the row was marked PUBLISHED")
    }

    @Test
    fun a_fix_captured_offline_is_buffered_rather_than_dropped() {
        val pipeline = pipeline()
        transport.isConnected = false

        val outcome = pipeline.onFix(start, fix(6.9271, 79.8612))

        assertIs<FixOutcome.Buffered>(outcome)
        assertTrue(transport.live.isEmpty(), "nothing went out")
        // R-17: the row exists on disk before anything is attempted, so a process kill here still
        // replays the sample.
        assertEquals(1, journal.rows.size, "the fix is on disk")
    }

    @Test
    fun the_cadence_is_the_phase_default_and_a_fix_inside_it_is_skipped() {
        val pipeline = pipeline()
        transport.isConnected = true

        // D5' §5.2 / AL-12: PickupBound is a 4 s cadence, and the near-pickup geofence burst is 1 s.
        pipeline.onPhase(GpsPhase.ACCEPTED_PICKUP_BOUND)
        assertEquals(4.seconds, pipeline.intervalAt(start), "accepted → pickup-bound cadence")

        pipeline.onFix(start, fix(6.9271, 79.8612))
        val tooSoon = pipeline.onFix(start + 1.seconds, fix(6.9280, 79.8620))
        assertIs<FixOutcome.Skipped>(tooSoon)
        assertEquals("TOO_SOON", tooSoon.reason)

        val onTime = pipeline.onFix(start + 4.seconds, fix(6.9290, 79.8630))
        assertIs<FixOutcome.Published>(onTime)

        pipeline.onPhase(GpsPhase.NEAR_PICKUP_GEOFENCE)
        assertEquals(1.seconds, pipeline.intervalAt(start), "AL-12's geofence burst")
    }

    @Test
    fun a_skipped_fix_consumes_no_seq() {
        val pipeline = pipeline()
        transport.isConnected = true
        pipeline.onPhase(GpsPhase.ACCEPTED_PICKUP_BOUND)

        val first = pipeline.onFix(start, fix(6.9271, 79.8612))
        assertIs<FixOutcome.Published>(first)

        pipeline.onFix(start + 1.seconds, fix(6.9280, 79.8620)) // skipped

        val second = pipeline.onFix(start + 4.seconds, fix(6.9290, 79.8630))
        assertIs<FixOutcome.Published>(second)

        // The counter numbers what was captured for the wire. Burning one per rejected tick would
        // run the server's watermark ahead of the samples it exists to order.
        assertEquals(first.sample.seq + 1, second.sample.seq, "seq is contiguous across a skip")
    }

    @Test
    fun the_backlog_replays_in_monotonic_seq_order_after_a_reconnect() {
        val pipeline = pipeline()
        transport.isConnected = false
        pipeline.onPhase(GpsPhase.IN_PROGRESS)

        // Six minutes of a tunnel at the 4 s IN_PROGRESS cadence.
        var at = start
        repeat(FIXES_WHILE_OFFLINE) {
            pipeline.onFix(at, fix(6.9271 + it * 0.001, 79.8612))
            at += 4.seconds
        }
        assertEquals(FIXES_WHILE_OFFLINE.toLong(), journal.size(), "everything is on disk")

        transport.isConnected = true
        pipeline.onReconnected(at)

        // The 2 s live-drain window (R-09): the vehicle's CURRENT position goes first, so nothing
        // replays for two seconds after the socket comes back.
        assertEquals(0, pipeline.drainReplay(at), "replay is locked during the live-drain window")

        at += 3.seconds
        var drained = 0
        repeat(DRAIN_TICKS) {
            drained += pipeline.drainReplay(at)
            at += 1.seconds
        }

        assertEquals(FIXES_WHILE_OFFLINE, drained, "the whole backlog replayed")
        assertTrue(transport.live.isEmpty(), "the backlog never touched the live topic")

        val seqs = transport.replay.map(PositionSample::seq)
        assertEquals(seqs.sorted(), seqs, "replay is in seq order")
        assertEquals(seqs.distinct(), seqs, "no sample was replayed twice")
    }

    @Test
    fun a_replay_publish_that_fails_leaves_the_sample_for_the_next_attempt() {
        val pipeline = pipeline()
        transport.isConnected = false
        repeat(3) { pipeline.onFix(start + (it * 60).seconds, fix(6.9271 + it * 0.01, 79.8612)) }

        transport.isConnected = true
        pipeline.onReconnected(start)
        transport.failReplayAfter = 1

        val at = start + 3.seconds
        assertEquals(1, pipeline.drainReplay(at), "one went out before the failure")
        assertTrue(journal.interrupted, "the un-confirmed rows went back to PENDING")

        transport.failReplayAfter = Int.MAX_VALUE
        val rest = pipeline.drainReplay(at + 1.seconds)
        assertEquals(2, rest, "the remaining two follow")
        assertEquals(listOf(1L, 2L, 3L), transport.replay.map(PositionSample::seq), "still in order, none lost")
    }

    private fun pipeline() = PositionPipeline(
        transport = transport,
        journal = journal,
        engine = AdaptiveRateEngine(),
        queue = PositionReplayQueue(),
    )

    private fun fix(lat: Double, lng: Double, at: Timestamp = start) =
        Fix(lat = lat, lng = lng, sampleTs = at, accuracyM = 8.0)

    private companion object {
        const val FIXES_WHILE_OFFLINE = 90

        /** Enough one-second ticks to clear 90 samples at the 20/s replay ceiling, with slack. */
        const val DRAIN_TICKS = 12
    }
}

/** A broker that records what it was given and can be told to fail. */
private class FakeTransport : PositionTransport {

    override var isConnected: Boolean = false
    var failReplayAfter: Int = Int.MAX_VALUE

    val live = mutableListOf<PositionSample>()
    val replay = mutableListOf<PositionSample>()

    override fun publishLive(sample: PositionSample): Boolean {
        if (!isConnected) return false
        live += sample
        return true
    }

    override fun publishReplay(sample: PositionSample): Boolean {
        if (!isConnected || replay.size >= failReplayAfter) return false
        replay += sample
        return true
    }
}

/**
 * `gps_buffer` in memory.
 *
 * The counter is strictly increasing and never reset, which is the property C018's
 * `PersistentPositionSequencer` provides on disk; the durability itself is asserted in `:shared`'s
 * own suite, and repeating it here would be testing SQLDelight.
 */
private class FakeJournal(private val vehicleId: Ulid) : PositionJournal {

    val rows = linkedMapOf<Long, PositionSample>()
    val published = mutableSetOf<Long>()
    private val inFlight = mutableSetOf<Long>()
    var interrupted = false
        private set

    private var seq = 0L

    override fun record(fix: Fix, now: Timestamp): PositionSample {
        val sample = PositionSample(
            vehicleId = vehicleId,
            sampleTs = fix.sampleTs,
            seq = ++seq,
            lat = fix.lat,
            lng = fix.lng,
            accuracyM = fix.accuracyM,
            source = PositionSource.MOBILE,
        )
        rows[sample.seq] = sample
        return sample
    }

    override fun onPublishedLive(seq: Long) {
        published += seq
        rows.remove(seq)
    }

    override fun replayBatch(limit: Int): List<PositionSample> = rows.values.sortedBy(PositionSample::seq).take(limit)

    override fun onReplayStarted(seqs: List<Long>) {
        inFlight += seqs
    }

    override fun onReplayAcked(seq: Long) {
        rows.keys.filter { it <= seq }.forEach(rows::remove)
        inFlight.removeAll { it <= seq }
    }

    override fun onReplayInterrupted() {
        interrupted = true
        inFlight.clear()
    }

    override fun size(): Long = rows.size.toLong()
}
