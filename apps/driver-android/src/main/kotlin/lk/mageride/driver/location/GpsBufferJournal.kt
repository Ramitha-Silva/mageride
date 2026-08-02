package lk.mageride.driver.location

import lk.mageride.shared.data.models.PositionSample
import lk.mageride.shared.data.models.PositionSource
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.db.GpsBuffer

/**
 * [PositionJournal] over C018's `gps_buffer` table.
 *
 * A thin adapter and nothing more — every rule it looks like it is applying belongs to
 * [GpsBuffer]: the block-reserving `PersistentPositionSequencer` behind `record`, the
 * `PENDING → REPLAY_PENDING → ACKED` states, the `seq`-ordered `replayBatch`, and §4.3's three
 * eviction bounds. The interface exists so [PositionPipeline] is testable without SQLite, not
 * because there was a second design.
 */
internal class GpsBufferJournal(private val buffer: GpsBuffer) : PositionJournal {

    override fun record(fix: Fix, now: Timestamp): PositionSample = buffer.record(
        lat = fix.lat,
        lng = fix.lng,
        sampleTs = fix.sampleTs,
        now = now,
        accuracyM = fix.accuracyM,
        speedMps = fix.speedMps,
        headingDeg = fix.headingDeg,
        satCount = fix.satCount,
        // MOBILE, always: this stream is a handset. A tracker's samples arrive through C043 with
        // their own source, and `telemetry.positions.source` is what tells the two apart.
        source = PositionSource.MOBILE,
    ).toSample()

    override fun onPublishedLive(seq: Long) {
        buffer.onPublishedLive(seq)
    }

    override fun replayBatch(limit: Int): List<PositionSample> = buffer.replayBatch(limit).map { it.toSample() }

    override fun onReplayStarted(seqs: List<Long>) {
        buffer.onReplayStarted(seqs)
    }

    override fun onReplayAcked(seq: Long) {
        buffer.onReplayAcked(seq)
    }

    override fun onReplayInterrupted() {
        buffer.onReplayInterrupted()
    }

    override fun size(): Long = buffer.size()
}
