package lk.mageride.shared.db

import lk.mageride.shared.mqtt.PositionSequencer
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes

/**
 * §1.5 — the `seq` watermark.
 *
 * "If it rewinds, `position-processor-svc` discards everything published afterwards and the
 * vehicle goes dark while the app believes it is publishing" (C017's handoff, R-17/T-05). Every
 * assertion here is a way that could happen.
 */
class PersistentPositionSequencerTest {

    private val vehicle = "veh-1"

    @Test
    fun the_counter_starts_at_one_on_a_fresh_install() {
        val sequencer = PersistentPositionSequencer(FakeMetaStore(), vehicle)

        assertEquals(0, sequencer.last)
        assertEquals(1, sequencer.next(T0))
        assertEquals(2, sequencer.next(T0))
    }

    @Test
    fun the_counter_never_rewinds_across_a_restart() {
        val meta = FakeMetaStore()
        val before = PersistentPositionSequencer(meta, vehicle)
        repeat(250) { before.next(T0) }
        val lastHandedOut = before.last

        // ---- process dies; a new sequencer reads what is on disk ----
        val after = PersistentPositionSequencer(meta, vehicle)

        assertTrue(after.next(T0) > lastHandedOut, "restart handed out ${after.last} after $lastHandedOut")
    }

    @Test
    fun a_restart_skips_the_unused_tail_of_the_reserved_block_and_that_is_fine() {
        val meta = FakeMetaStore()
        val before = PersistentPositionSequencer(meta, vehicle, blockSize = 100)
        before.next(T0) // reserves 1..101

        val after = PersistentPositionSequencer(meta, vehicle, blockSize = 100)

        // A gap, not a rewind: the server's watermark is a floor and the unique index only rejects
        // duplicates, so skipped values cost nothing.
        assertEquals(102, after.next(T0))
    }

    @Test
    fun the_watermark_is_written_once_per_block_not_once_per_sample() {
        val meta = FakeMetaStore()
        val sequencer = PersistentPositionSequencer(meta, vehicle, blockSize = 100)

        repeat(100) { sequencer.next(T0) }

        // One write for 100 fixes. At AL-12's 1 s near-geofence burst, that is one write per 100 s
        // instead of one per second.
        assertEquals(1, meta.writes)
    }

    @Test
    fun a_buffer_ahead_of_the_watermark_raises_the_floor() {
        val meta = FakeMetaStore()
        // A database restored from a backup: rows exist that the watermark does not know about.
        val sequencer = PersistentPositionSequencer(meta, vehicle, floor = 5_000)

        assertEquals(5_001, sequencer.next(T0))
    }

    @Test
    fun observing_a_higher_seq_moves_the_counter_and_a_lower_one_does_not() {
        val sequencer = PersistentPositionSequencer(FakeMetaStore(), vehicle)
        sequencer.next(T0)

        sequencer.observe(900, T0)
        assertEquals(901, sequencer.next(T0))

        sequencer.observe(5, T0)
        assertEquals(902, sequencer.next(T0))
    }

    @Test
    fun two_vehicles_keep_separate_counters() {
        val meta = FakeMetaStore()
        val a = PersistentPositionSequencer(meta, "veh-a")
        val b = PersistentPositionSequencer(meta, "veh-b")

        repeat(5) { a.next(T0) }

        // §1.5 says "monotonic per vehicle_id", and gps_buffer's primary key is (vehicle_id, seq).
        // One shared counter would move veh-b's sequence on without the server ever seeing it.
        assertEquals(1, b.next(T0))
        assertEquals(6, a.next(T0))
    }

    @Test
    fun it_is_the_persistent_half_of_C017s_in_memory_sequencer() {
        // C017's PositionSequencer says "C018 persists last; construct this with the stored
        // value". This is the handshake: the same start, the same strictly-increasing rule.
        val meta = FakeMetaStore()
        val persistent = PersistentPositionSequencer(meta, vehicle)
        repeat(3) { persistent.next(T0) }

        val inMemory = PositionSequencer(start = persistent.last)

        assertEquals(persistent.last + 1, inMemory.next())
    }
}

/** §1.5 + §4.3 — the ring itself, on the in-memory store. The SQL is exercised in androidHostTest. */
class GpsBufferRulesTest {

    private val vehicle = "veh-1"

    private fun buffer(
        store: FakeGpsBufferStore = FakeGpsBufferStore(),
        policy: GpsRetentionPolicy = GpsRetentionPolicy(),
    ) = GpsBuffer(store, PersistentPositionSequencer(FakeMetaStore(), vehicle), vehicle, policy)

    @Test
    fun every_recorded_fix_gets_a_strictly_increasing_seq() {
        val buffer = buffer()

        val seqs = (1..10).map { buffer.record(6.9, 79.8, T0, T0).seq }

        assertEquals(seqs.sorted(), seqs)
        assertEquals(seqs.distinct(), seqs)
    }

    @Test
    fun the_replay_batch_comes_out_in_seq_order() {
        val store = FakeGpsBufferStore()
        val buffer = buffer(store)
        repeat(5) { buffer.record(6.9, 79.8, T0, T0) }

        val batch = buffer.replayBatch()

        assertEquals(listOf(1L, 2L, 3L, 4L, 5L), batch.map { it.seq })
    }

    @Test
    fun a_published_fix_leaves_the_replay_backlog() {
        val buffer = buffer()
        val fix = buffer.record(6.9, 79.8, T0, T0)
        buffer.record(6.91, 79.81, T0, T0)

        buffer.onPublishedLive(fix.seq)

        assertEquals(listOf(2L), buffer.replayBatch().map { it.seq })
    }

    @Test
    fun an_interrupted_drain_puts_its_samples_back() {
        val buffer = buffer()
        repeat(3) { buffer.record(6.9, 79.8, T0, T0) }
        val batch = buffer.replayBatch()
        buffer.onReplayStarted(batch.map { it.seq })

        buffer.onReplayInterrupted()

        assertEquals(listOf(1L, 2L, 3L), buffer.replayBatch().map { it.seq })
    }

    @Test
    fun an_ack_clears_everything_up_to_and_including_the_confirmed_seq() {
        val buffer = buffer()
        repeat(5) { buffer.record(6.9, 79.8, T0, T0) }

        buffer.onReplayAcked(3)

        assertEquals(listOf(4L, 5L), buffer.replayBatch().map { it.seq })
    }

    @Test
    fun eviction_removes_delivered_rows_first() {
        val buffer = buffer()
        repeat(4) { buffer.record(6.9, 79.8, T0, T0) }
        buffer.onReplayAcked(2)

        val evicted = buffer.evict(T0)

        assertEquals(2, evicted.delivered)
        assertEquals(listOf(3L, 4L), buffer.snapshot().map { it.seq })
    }

    @Test
    fun eviction_drops_samples_older_than_the_age_cap() {
        val buffer = buffer(policy = GpsRetentionPolicy(maxAge = 6.hours))
        buffer.record(6.9, 79.8, T0, T0)
        buffer.record(6.9, 79.8, T0 + 5.hours, T0 + 5.hours)

        val evicted = buffer.evict(T0 + 7.hours)

        assertEquals(1, evicted.aged)
        assertEquals(listOf(2L), buffer.snapshot().map { it.seq })
    }

    @Test
    fun eviction_enforces_the_row_cap_oldest_first_and_leaves_a_contiguous_run() {
        val buffer = buffer(policy = GpsRetentionPolicy(maxRows = 5))
        repeat(12) { buffer.record(6.9, 79.8, T0, T0) }

        val evicted = buffer.evict(T0)

        assertEquals(7, evicted.overflow)
        val survivors = buffer.snapshot().map { it.seq }
        assertEquals(listOf(8L, 9L, 10L, 11L, 12L), survivors)
        // The point of "oldest first BY SEQ": what is left is still an ascending run with no hole,
        // so the server's per-vehicle watermark advances over it cleanly.
        assertEquals(survivors, survivors.sorted())
        assertEquals(survivors.size.toLong(), (survivors.last() - survivors.first() + 1))
    }

    @Test
    fun a_duplicate_seq_is_dropped_locally_rather_than_reaching_the_wire() {
        val store = FakeGpsBufferStore()
        val buffer = buffer(store)
        val fix = buffer.record(6.9, 79.8, T0, T0)

        buffer.record(fix.toSample(), T0)

        assertEquals(1, store.rows.size)
    }

    @Test
    fun recording_a_sample_that_already_carries_a_seq_raises_the_counter() {
        val buffer = buffer()
        buffer.record(6.9, 79.8, T0, T0)

        buffer.record(
            buffer.snapshot().first().copy(seq = 500).toSample(),
            T0 + 1.minutes,
        )

        assertEquals(501, buffer.record(6.9, 79.8, T0, T0).seq)
    }

    @Test
    fun a_buffered_fix_round_trips_to_the_canonical_position_payload() {
        val buffer = buffer()
        val fix = buffer.record(
            lat = 6.9271,
            lng = 79.8612,
            sampleTs = T0,
            now = T0,
            accuracyM = 12.5,
            speedMps = 8.3,
            headingDeg = 275,
            satCount = 9,
        )

        val sample = fix.toSample()

        assertEquals(fix.seq, sample.seq)
        assertEquals(fix.vehicleId, sample.vehicleId)
        assertEquals(6.9271, sample.lat)
        assertEquals(275, sample.headingDeg)
        assertEquals(T0, sample.sampleTs)
    }
}
