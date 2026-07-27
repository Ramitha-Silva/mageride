package lk.mageride.shared.mqtt

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** The offline buffer and its replay discipline (R-17, T-05, R-09). */
class PositionReplayQueueTest {

    @Test
    fun the_sequencer_is_strictly_monotonic() {
        val sequencer = PositionSequencer()

        val issued = List(5) { sequencer.next() }

        assertEquals(listOf(1L, 2L, 3L, 4L, 5L), issued)
        assertEquals(5L, sequencer.last)
        assertTrue(issued.zipWithNext().all { (a, b) -> b > a })
    }

    @Test
    fun the_sequencer_resumes_from_stored_state_and_never_rewinds() {
        // If it rewound, every sample after a restart would carry a seq the server has already
        // seen and position-processor-svc would discard all of them — the vehicle goes dark while
        // the app believes it is publishing.
        val sequencer = PositionSequencer(start = 84_213)

        assertEquals(84_214L, sequencer.next())

        sequencer.observe(90_000)
        assertEquals(90_001L, sequencer.next())

        sequencer.observe(10)
        assertEquals(90_002L, sequencer.next(), "observing an older seq cannot lower the counter")
    }

    @Test
    fun duplicates_are_dropped_locally_rather_than_put_on_the_radio() {
        val queue = PositionReplayQueue()

        assertEquals(BufferOutcome.BUFFERED, queue.buffer(sample(seq = 10)))
        assertEquals(BufferOutcome.DUPLICATE, queue.buffer(sample(seq = 10)))
        assertEquals(BufferOutcome.DUPLICATE, queue.buffer(sample(seq = 9)), "out of order is a duplicate too")
        assertEquals(BufferOutcome.BUFFERED, queue.buffer(sample(seq = 11)))
        assertEquals(2, queue.size)
        assertEquals(11L, queue.highestSeq)
    }

    @Test
    fun everything_that_leaves_the_queue_carries_a_strictly_increasing_seq() {
        val queue = PositionReplayQueue()
        listOf(1L, 2L, 2L, 5L, 3L, 6L).forEach { queue.buffer(sample(seq = it)) }
        queue.onReconnected(MQTT_EPOCH)

        val drained = buildList {
            var now = MQTT_EPOCH + 3.seconds
            while (queue.peek(now) != null) {
                add(queue.onPublished(now)!!.seq)
                now += 100.milliseconds
            }
        }

        assertEquals(listOf(1L, 2L, 5L, 6L), drained)
    }

    @Test
    fun the_ring_evicts_its_oldest_sample_when_it_is_full() {
        val queue = PositionReplayQueue(capacity = 3)
        (1L..3L).forEach { queue.buffer(sample(seq = it)) }

        assertEquals(BufferOutcome.BUFFERED_WITH_EVICTION, queue.buffer(sample(seq = 4)))
        assertEquals(3, queue.size)

        queue.onReconnected(MQTT_EPOCH)
        assertEquals(2L, queue.peek(MQTT_EPOCH + 3.seconds)?.seq, "the oldest went, not the newest")
    }

    @Test
    fun replay_stays_locked_for_two_seconds_after_a_reconnect() {
        // "On reconnect the device opens an idle MQTT session first, drains live samples for 2 s,
        // then unlocks replay" (ADD §7.5.3).
        val queue = PositionReplayQueue()
        queue.buffer(sample(seq = 1))
        queue.onReconnected(MQTT_EPOCH)

        assertTrue(queue.isDraining(MQTT_EPOCH + 1.seconds))
        assertNull(queue.peek(MQTT_EPOCH + 1.seconds), "a backlog must not arrive before the live stream")

        assertFalse(queue.isDraining(MQTT_EPOCH + 2.seconds))
        assertNotNull(queue.peek(MQTT_EPOCH + 2.seconds))
    }

    @Test
    fun live_preempts_replay_four_to_one_while_both_are_flowing() {
        val queue = PositionReplayQueue()
        (1L..10L).forEach { queue.buffer(sample(seq = it)) }
        queue.onReconnected(MQTT_EPOCH)
        val now = MQTT_EPOCH + 3.seconds

        assertNull(queue.peek(now, livePending = true), "live goes first")
        repeat(3) { queue.onLivePublished() }
        assertNull(queue.peek(now, livePending = true), "three is not four")

        queue.onLivePublished()
        assertNotNull(queue.peek(now, livePending = true), "the fourth live publish earns a replay slot")

        queue.onPublished(now)
        assertNull(queue.peek(now, livePending = true), "and the credit is spent")
    }

    @Test
    fun a_backlog_drains_at_full_rate_when_no_live_sample_is_waiting() {
        // Weighted fair share, not a hard gate: a vehicle parked with a backlog would otherwise
        // never finish replaying, because nothing is generating live publishes to earn credit.
        val queue = PositionReplayQueue()
        (1L..5L).forEach { queue.buffer(sample(seq = it)) }
        queue.onReconnected(MQTT_EPOCH)

        assertNotNull(queue.peek(MQTT_EPOCH + 3.seconds, livePending = false))
    }

    @Test
    fun replay_is_capped_at_twenty_samples_a_second() {
        val queue = PositionReplayQueue()
        (1L..40L).forEach { queue.buffer(sample(seq = it)) }
        queue.onReconnected(MQTT_EPOCH)
        val now = MQTT_EPOCH + 3.seconds

        repeat(MqttRateLimits.REPLAY_MSG_PER_SECOND) {
            assertNotNull(queue.peek(now))
            queue.onPublished(now)
        }

        assertNull(queue.peek(now), "the 21st in the same second waits")
        assertNotNull(queue.peek(now + 1.seconds), "the window slides")
    }

    @Test
    fun clearing_empties_the_backlog() {
        val queue = PositionReplayQueue()
        queue.buffer(sample(seq = 1))
        queue.clear()

        assertTrue(queue.isEmpty)
        assertNull(queue.onPublished(MQTT_EPOCH))
    }

    @Test
    fun the_defaults_are_the_numbers_the_specs_fix() {
        assertEquals(50_000, PositionReplayQueue.RING_CAPACITY, "D6' §4.4 flash ring")
        assertEquals(4, PositionReplayQueue.LIVE_PREEMPT_RATIO, "D6' §3.5")
        assertEquals(2.seconds, PositionReplayQueue.LIVE_DRAIN_WINDOW, "R-09")
        assertEquals(20, MqttRateLimits.REPLAY_MSG_PER_SECOND, "D-17")
        assertEquals(5, MqttRateLimits.LIVE_MSG_PER_SECOND, "D-17")
    }
}
