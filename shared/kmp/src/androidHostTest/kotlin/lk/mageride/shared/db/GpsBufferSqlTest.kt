package lk.mageride.shared.db

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.seconds

/**
 * The C018 Definition of Done, item 3: *the `gps_buffer` evicts by age/size per §4.3 without losing
 * ordering* — and item 2's sibling guarantee, that the `seq` watermark survives a restart.
 *
 * Against a real SQLite, because every rule here is enforced by SQL: the composite primary key
 * `(vehicle_id, seq)`, the `ORDER BY seq` in the replay query, and the `ORDER BY seq LIMIT n`
 * subquery the row cap deletes through.
 */
class GpsBufferSqlTest {

    private val vehicle = "01JZVEHICLE0000000000000001"

    @Test
    fun the_replay_backlog_comes_out_in_seq_order_whatever_order_it_went_in() {
        openDriverDb().use { db ->
            val buffer = db.gpsBuffer(vehicle)
            // Insert out of order, straight through the store, to prove the ORDER BY is doing the
            // work rather than the insertion order.
            listOf(5L, 1L, 4L, 2L, 3L).forEach { seq ->
                db.sql.gpsBufferQueries.append(
                    seq = seq, vehicle_id = vehicle, lat = 6.9, lng = 79.8,
                    accuracy_m = null, speed_mps = null, heading_deg = null, hdop = null,
                    sat_count = null, sample_ts = NOW, source = 0,
                    state = GpsSampleState.PENDING.name, created_at = NOW,
                )
            }

            assertEquals(listOf(1L, 2L, 3L, 4L, 5L), buffer.replayBatch().map { it.seq })
        }
    }

    @Test
    fun the_seq_watermark_survives_a_restart_and_never_rewinds() {
        val file = tempDbFile("gps-seq")
        val handedOut = openDriverDb(file).use { db ->
            val buffer = db.gpsBuffer(vehicle)
            repeat(30) { buffer.record(6.9, 79.8, NOW, NOW) }
            buffer.lastSeq
        }

        openDriverDb(file).use { db ->
            val next = db.gpsBuffer(vehicle).record(6.9, 79.8, NOW, NOW).seq

            // Strictly greater. A rewind would make position-processor-svc discard everything the
            // app published afterwards — the vehicle goes dark while the app thinks it is fine.
            assertTrue(next > handedOut, "restart handed out $next after $handedOut")
        }
    }

    @Test
    fun the_watermark_still_rises_when_the_backlog_has_been_reaped() {
        val file = tempDbFile("gps-seq-empty")
        val handedOut = openDriverDb(file).use { db ->
            val buffer = db.gpsBuffer(vehicle)
            repeat(10) { buffer.record(6.9, 79.8, NOW, NOW) }
            buffer.onReplayAcked(buffer.lastSeq)
            buffer.evict(NOW)
            assertEquals(0, buffer.size())
            buffer.lastSeq
        }

        openDriverDb(file).use { db ->
            // meta('gps.seq.{vehicleId}') is the only thing left holding the sequence — the rows it
            // could have been recovered from are gone.
            assertTrue(db.gpsBuffer(vehicle).record(6.9, 79.8, NOW, NOW).seq > handedOut)
        }
    }

    @Test
    fun a_duplicate_seq_is_dropped_by_the_primary_key() {
        openDriverDb().use { db ->
            val buffer = db.gpsBuffer(vehicle)
            val fix = buffer.record(6.9, 79.8, NOW, NOW)

            buffer.record(fix.toSample(), NOW)
            buffer.record(fix.toSample(), NOW)

            assertEquals(1, buffer.size())
        }
    }

    @Test
    fun two_vehicles_keep_independent_sequences_and_independent_backlogs() {
        openDriverDb().use { db ->
            val a = db.gpsBuffer("veh-a")
            val b = db.gpsBuffer("veh-b")

            repeat(3) { a.record(6.9, 79.8, NOW, NOW) }
            val firstForB = b.record(6.9, 79.8, NOW, NOW)

            assertEquals(1, firstForB.seq)
            assertEquals(3, a.size())
            assertEquals(1, b.size())
        }
    }

    @Test
    fun eviction_removes_delivered_then_aged_then_overflow_and_leaves_a_contiguous_run() {
        openDriverDb().use { db ->
            val buffer = GpsBuffer(
                store = db.gpsStore,
                sequencer = PersistentPositionSequencer(db.meta, vehicle),
                vehicleId = vehicle,
                policy = GpsRetentionPolicy(maxAge = 6.hours, maxRows = 4),
            )
            // 3 old, then 10 recent.
            repeat(3) { buffer.record(6.9, 79.8, NOW - 8.hours, NOW - 8.hours) }
            repeat(10) { buffer.record(6.9, 79.8, NOW, NOW) }
            buffer.onReplayAcked(2) // the first two are confirmed

            val evicted = buffer.evict(NOW)

            assertEquals(2, evicted.delivered)
            assertEquals(1, evicted.aged, "the third old sample is past the 6 h cap")
            assertEquals(6, evicted.overflow, "10 recent - the 4-row cap")

            val survivors = buffer.snapshot().map { it.seq }
            assertEquals(listOf(10L, 11L, 12L, 13L), survivors)
            // "without losing ordering": every rule deletes a PREFIX of the ascending run, so the
            // server's per-vehicle watermark advances over what is left without a gap.
            assertEquals(survivors.sorted(), survivors)
            assertEquals(survivors.size.toLong(), survivors.last() - survivors.first() + 1)
        }
    }

    @Test
    fun eviction_only_touches_the_vehicle_it_was_asked_about() {
        openDriverDb().use { db ->
            val a = db.gpsBuffer("veh-a")
            val b = db.gpsBuffer("veh-b")
            repeat(5) { a.record(6.9, 79.8, NOW - 9.hours, NOW - 9.hours) }
            repeat(5) { b.record(6.9, 79.8, NOW, NOW) }

            a.evict(NOW)

            assertEquals(0, a.size())
            assertEquals(5, b.size())
        }
    }

    @Test
    fun the_retention_sweep_bounds_every_vehicle_that_still_holds_rows() {
        openDriverDb().use { db ->
            listOf("veh-a", "veh-b", "veh-c").forEach { id ->
                repeat(4) { db.gpsBuffer(id).record(6.9, 79.8, NOW - 9.hours, NOW - 9.hours) }
            }

            val report = db.retention.sweep(NOW)

            assertEquals(12, report.removed[RetentionTable.GPS_BUFFER])
        }
    }

    @Test
    fun an_interrupted_drain_puts_its_samples_back_in_seq_order() {
        openDriverDb().use { db ->
            val buffer = db.gpsBuffer(vehicle)
            repeat(6) { buffer.record(6.9, 79.8, NOW, NOW) }
            val batch = buffer.replayBatch(limit = 3)
            buffer.onReplayStarted(batch.map { it.seq })

            buffer.onReplayInterrupted()

            assertEquals(listOf(1L, 2L, 3L, 4L, 5L, 6L), buffer.replayBatch().map { it.seq })
        }
    }

    @Test
    fun a_live_published_sample_leaves_the_backlog_but_an_unpublished_neighbour_does_not() {
        openDriverDb().use { db ->
            val buffer = db.gpsBuffer(vehicle)
            val first = buffer.record(6.9, 79.8, NOW, NOW)
            buffer.record(6.91, 79.81, NOW + 1.seconds, NOW + 1.seconds)

            buffer.onPublishedLive(first.seq)

            assertEquals(listOf(2L), buffer.replayBatch().map { it.seq })
            assertEquals(2, buffer.size(), "still stored until the sweep reaps it")
            assertEquals(1, buffer.evict(NOW).delivered)
        }
    }

    @Test
    fun the_stored_state_domain_is_the_one_the_spec_prints() {
        openDriverDb().use { db ->
            GpsSampleState.entries.forEachIndexed { index, state ->
                db.sql.gpsBufferQueries.append(
                    seq = (index + 1).toLong(), vehicle_id = vehicle, lat = 6.9, lng = 79.8,
                    accuracy_m = null, speed_mps = null, heading_deg = null, hdop = null,
                    sat_count = null, sample_ts = NOW, source = 0,
                    state = state.name, created_at = NOW,
                )
            }
            assertEquals(GpsSampleState.entries.size, db.gpsBuffer(vehicle).snapshot().size)

            val rejected = runCatching {
                db.sql.gpsBufferQueries.append(
                    seq = 99, vehicle_id = vehicle, lat = 6.9, lng = 79.8,
                    accuracy_m = null, speed_mps = null, heading_deg = null, hdop = null,
                    sat_count = null, sample_ts = NOW, source = 0,
                    state = "SENT", created_at = NOW,
                )
                db.gpsBuffer(vehicle).snapshot().any { it.seq == 99L }
            }
            // `append` is INSERT OR IGNORE, so a bad state is dropped rather than raised — that is
            // the documented trade for cheap duplicate-seq handling. Either way it never lands.
            assertTrue(rejected.getOrDefault(false) == false, "a state outside the CHECK was stored")
        }
    }
}
