-- =====================================================================================
-- 1801 — telemetry: the high-frequency tracker hypertable (T-05, T-06, T-10)
-- Source: server_db_schema.md §18 · D4' §17 · ADD §9.5 · ADD §9.2 / server_db_schema §21
--
-- This is the historical telematics system of record: hardware trackers (GT06 / JT808 /
-- H02 / NMEA-over-MQTT) plus mobile GPS, at two orders of magnitude the volume the
-- operational schemas are sized for. It is deliberately separate from them — §21 and ADD
-- §9.2 both state that high-frequency raw GPS never lands in a Postgres operational table;
-- only the 1/min sample (trips.position_samples, C004) and the trip summary do.
--
-- No application logic lives here: persistence-writer-svc (C040) owns the write path and
-- batches ~1k rows / 500 ms / partition through COPY (ADD §9.5 item 5).
--
-- SPEC DIVERGENCE — the unique index (micro-change-set, see build/progress.md):
--   Both DDL sources print `CREATE UNIQUE INDEX ON telemetry.positions (vehicle_id, seq)`.
--   TimescaleDB rejects that outright on this hypertable —
--     ERROR: cannot create a unique index without the column "sample_ts" (used in partitioning)
--   because every unique index must contain all partitioning columns. The replay-dedupe key
--   therefore lands as (vehicle_id, seq, sample_ts), which still rejects the case T-05/R-17
--   exists for: a tracker re-sending a buffered sample carries the same GNSS timestamp it
--   was captured with, so the row is identical in all three columns.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS telemetry.positions (
  -- No FK to registry.vehicles: this is a COPY-batched ingest path at 40k rows/s (§21) and
  -- an FK costs an index probe per row. Trackers are resolved to a vehicle upstream, by
  -- prov.tracker_bindings in the ingest plane (T-02/T-03).
  vehicle_id   UUID NOT NULL,
  sample_ts    TIMESTAMPTZ NOT NULL,                     -- GNSS UTC — the hypertable time dimension
  received_ts  TIMESTAMPTZ NOT NULL DEFAULT now(),       -- when the platform saw it; sample_ts - received_ts is the replay lag
  seq          BIGINT NOT NULL,                          -- monotonic per vehicle (replay dedup, T-05/R-17)
  lat          DOUBLE PRECISION NOT NULL,
  lng          DOUBLE PRECISION NOT NULL,
  speed_mps    REAL,
  heading_deg  SMALLINT,
  accuracy_m   REAL,
  hdop         REAL,
  sat_count    SMALLINT,
  source       SMALLINT NOT NULL,                        -- 0=mobile,1=gt06,2=jt808,3=h02,4=nmea-mqtt
  fleet_id     UUID,                                     -- RLS / fleet scoping (Epic 13); NULL = not fleet-owned
  trip_id      UUID,
  -- The specs print no constraints. These four encode what their column comments already
  -- state, and each one is a plain tuple check: no index probe, so the COPY path is unaffected.
  -- A cheap tracker that reports 0/999 degrees or a negative sequence is a bug the ingest
  -- plane (C039 position-processor-svc) must filter before the batch reaches here.
  CONSTRAINT ck_positions_lat    CHECK (lat BETWEEN -90 AND 90),
  CONSTRAINT ck_positions_lng    CHECK (lng BETWEEN -180 AND 180),
  CONSTRAINT ck_positions_seq    CHECK (seq >= 0),
  CONSTRAINT ck_positions_source CHECK (source BETWEEN 0 AND 4)
);

-- 1-day chunks × 16 vehicle_id hash partitions (ADD §9.5 item 1, §21).
SELECT create_hypertable('telemetry.positions', 'sample_ts',
                         partitioning_column => 'vehicle_id',
                         number_partitions   => 16,
                         chunk_time_interval => INTERVAL '1 day',
                         if_not_exists       => TRUE);

-- "last position for vehicle X" / the per-vehicle history scan (ADD §9.5 item 6).
CREATE INDEX IF NOT EXISTS ix_positions_vehicle_ts
  ON telemetry.positions (vehicle_id, sample_ts DESC);

-- Fleet Portal reads, and the only index the fleet-scoped view (1804) can use.
CREATE INDEX IF NOT EXISTS ix_positions_fleet_ts
  ON telemetry.positions (fleet_id, sample_ts DESC) WHERE fleet_id IS NOT NULL;

-- Replay idempotency (T-05/R-17). See the divergence note in the header for why sample_ts
-- is part of the key: TimescaleDB will not accept (vehicle_id, seq) on its own.
CREATE UNIQUE INDEX IF NOT EXISTS ux_positions_vehicle_seq
  ON telemetry.positions (vehicle_id, seq, sample_ts);

-- "trip linestring for trip Y" (ADD §9.5 item 6). Neither spec prints it; the query it backs
-- is named there. Partial, because only Mode A/B samples carry a trip.
CREATE INDEX IF NOT EXISTS ix_positions_trip_ts
  ON telemetry.positions (trip_id, sample_ts) WHERE trip_id IS NOT NULL;

COMMENT ON TABLE telemetry.positions IS
  'High-frequency tracker + mobile GPS, TimescaleDB hypertable (1-day chunks x 16 vehicle_id hash partitions). Historical telematics system of record (T-06). Raw positions never land in an operational schema (ADD 9.2); the 1/min operational sample is trips.position_samples. Written only by persistence-writer-svc (C040).';
COMMENT ON COLUMN telemetry.positions.seq IS
  'Monotonic per vehicle. Deduped by ux_positions_vehicle_seq for tracker replay (T-05/R-17).';
COMMENT ON COLUMN telemetry.positions.source IS
  '0=mobile, 1=gt06, 2=jt808, 3=h02, 4=nmea-mqtt (server_db_schema 18).';
COMMENT ON COLUMN telemetry.positions.fleet_id IS
  'Denormalised from registry.vehicles at write time so fleet scoping needs no join. NULL for a vehicle that belongs to no fleet; those rows are invisible to every fleet reader (1804).';
COMMENT ON INDEX telemetry.ux_positions_vehicle_seq IS
  'Replay idempotency (T-05/R-17). sample_ts is in the key because TimescaleDB requires every partitioning column in a unique index; a replayed sample carries its original GNSS timestamp, so the tuple still collides.';
