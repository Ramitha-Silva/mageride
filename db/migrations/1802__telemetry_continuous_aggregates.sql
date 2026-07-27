-- =====================================================================================
-- 1802 — telemetry: continuous aggregates, 1-min / 5-min / 1-hour + per-fleet health
-- Source: server_db_schema.md §18 · D4' §17 · ADD §9.5 items 2 and 6, T-06
--
-- The read path split (ADD §9.5 item 6): operational queries ("last position for vehicle X")
-- hit raw chunks inside the 30-day window; reporting queries hit these rollups. T-06 names
-- 1-min / 5-min / 1-hour; the printed DDL only carries the 1-min one, so the 5-min and
-- 1-hour rollups are landed here in the same column shape, so query-svc (C042) can pick a
-- granularity by table name alone.
--
-- Every aggregate is created WITH NO DATA. `CREATE MATERIALIZED VIEW ... WITH DATA` cannot
-- run inside a transaction block, and the migration runner gives each script one
-- (WithTransactionPerScript). WITH NO DATA is also the right shape for a migration: the
-- refresh policies below backfill in the background instead of materialising the whole
-- history while the deploy holds a lock.
--
-- `materialized_only = false` is set explicitly rather than left to the server default, so a
-- read combines the materialised buckets with the live tail. The Fleet Portal live map and
-- fleet-health-svc (C044) both read the current bucket, which by definition has not been
-- materialised yet.
--
-- The 5-min / 1-hour rollups are computed from the raw hypertable, not stacked on top of the
-- 1-min one. Rolling up avg(speed) from a coarser avg is only exact when every bucket holds
-- the same number of non-NULL speeds; speed_mps is nullable, so a hierarchical avg would be
-- subtly wrong. Three independent refreshes over raw cost more CPU and are correct.
-- =====================================================================================

-- 1-minute rollup — the shape both DDL sources print.
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.positions_1m
  WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
  SELECT vehicle_id,
         time_bucket('1 minute', sample_ts) AS bucket,
         avg(speed_mps)         AS avg_speed,
         max(speed_mps)         AS max_speed,
         count(*)               AS samples,
         last(lat, sample_ts)   AS last_lat,
         last(lng, sample_ts)   AS last_lng
    FROM telemetry.positions
   GROUP BY vehicle_id, bucket
  WITH NO DATA;

-- 5-minute rollup (T-06).
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.positions_5m
  WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
  SELECT vehicle_id,
         time_bucket('5 minutes', sample_ts) AS bucket,
         avg(speed_mps)         AS avg_speed,
         max(speed_mps)         AS max_speed,
         count(*)               AS samples,
         last(lat, sample_ts)   AS last_lat,
         last(lng, sample_ts)   AS last_lng
    FROM telemetry.positions
   GROUP BY vehicle_id, bucket
  WITH NO DATA;

-- 1-hour rollup (T-06). The reporting grain that outlives raw retention: raw chunks are
-- dropped at 30 days (1803), these buckets are kept for 12 months.
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.positions_1h
  WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
  SELECT vehicle_id,
         time_bucket('1 hour', sample_ts) AS bucket,
         avg(speed_mps)         AS avg_speed,
         max(speed_mps)         AS max_speed,
         count(*)               AS samples,
         last(lat, sample_ts)   AS last_lat,
         last(lng, sample_ts)   AS last_lng
    FROM telemetry.positions
   GROUP BY vehicle_id, bucket
  WITH NO DATA;

-- Per-fleet health rollup, read by fleet-health-svc (C044) and SCR-FP-* (US-3.13).
CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.fleet_health_5m
  WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
  SELECT fleet_id,
         time_bucket('5 minutes', sample_ts) AS bucket,
         count(DISTINCT vehicle_id) AS active_vehicles,
         count(*)                   AS samples
    FROM telemetry.positions
   WHERE fleet_id IS NOT NULL
   GROUP BY fleet_id, bucket
  WITH NO DATA;

-- ---------------------------------------------------------------------------------------
-- Refresh policies. Each window is (start_offset, end_offset] behind now(): end_offset keeps
-- the refresh off the bucket still being written, start_offset bounds how far back a late or
-- replayed sample (T-05) can still correct an already-materialised bucket. Every start_offset
-- stays well inside the 30-day raw retention (1803) so a refresh never reaches a dropped chunk.
-- ---------------------------------------------------------------------------------------

-- The 1-minute policy is the one both DDL sources print, verbatim.
SELECT add_continuous_aggregate_policy('telemetry.positions_1m',
         start_offset      => INTERVAL '3 hours',
         end_offset        => INTERVAL '1 minute',
         schedule_interval => INTERVAL '1 minute',
         if_not_exists     => TRUE);

SELECT add_continuous_aggregate_policy('telemetry.positions_5m',
         start_offset      => INTERVAL '1 day',
         end_offset        => INTERVAL '5 minutes',
         schedule_interval => INTERVAL '5 minutes',
         if_not_exists     => TRUE);

SELECT add_continuous_aggregate_policy('telemetry.positions_1h',
         start_offset      => INTERVAL '7 days',
         end_offset        => INTERVAL '1 hour',
         schedule_interval => INTERVAL '1 hour',
         if_not_exists     => TRUE);

-- Neither spec gives fleet_health_5m a policy — an omission, not a decision: without one the
-- view would only ever show the live tail through real-time aggregation and would never
-- materialise, so every fleet dashboard read would rescan raw chunks.
SELECT add_continuous_aggregate_policy('telemetry.fleet_health_5m',
         start_offset      => INTERVAL '1 day',
         end_offset        => INTERVAL '5 minutes',
         schedule_interval => INTERVAL '5 minutes',
         if_not_exists     => TRUE);

COMMENT ON VIEW telemetry.positions_1m IS
  'Per-vehicle 1-minute rollup (T-06). Continuous aggregate over telemetry.positions, refreshed every minute over a 3-hour window.';
COMMENT ON VIEW telemetry.positions_5m IS
  'Per-vehicle 5-minute rollup (T-06). Computed from raw, not from positions_1m — averaging an average is not exact when speed_mps is NULL.';
COMMENT ON VIEW telemetry.positions_1h IS
  'Per-vehicle 1-hour rollup (T-06). Retained 12 months, so it outlives the 30-day raw retention and is the reporting grain for anything older than a month.';
COMMENT ON VIEW telemetry.fleet_health_5m IS
  'Per-fleet 5-minute health rollup for fleet-health-svc (C044) and US-3.13. Scoped for fleet readers by telemetry.fleet_health_5m_fleet (1804) — this view is platform-wide.';
