-- =====================================================================================
-- 1803 — telemetry: compression and retention policies (T-06, T-10)
-- Source: server_db_schema.md §18 · D4' §17 · ADD §9.5 items 3 and 4 · ADD §9.2 / §21
--
-- ADD §9.5:
--   item 3 — compress chunks older than 7 days, segmentby vehicle_id, orderby sample_ts;
--            typical 10x reduction, still transparently queryable.
--   item 4 — hot retention 30 days at full resolution, aggregate retention 12 months;
--            raw chunks dropped after 30 days.
--
-- compress_orderby carries `seq` after `sample_ts DESC`. Both DDL sources print
-- `sample_ts DESC` alone, and TimescaleDB then warns:
--     WARNING: column "seq" should be used for segmenting or ordering
-- because ux_positions_vehicle_seq (1801) covers (vehicle_id, seq, sample_ts) and a column of
-- a unique index that is neither a segmentby nor an orderby column cannot be checked without
-- decompressing the whole batch. Adding seq to the orderby keeps the replay-dedupe guarantee
-- affordable on compressed chunks and leaves the segmentby exactly as specified.
-- =====================================================================================

ALTER TABLE telemetry.positions SET (
  timescaledb.compress,
  timescaledb.compress_segmentby = 'vehicle_id',
  timescaledb.compress_orderby   = 'sample_ts DESC, seq'
);

SELECT add_compression_policy('telemetry.positions', INTERVAL '7 days', if_not_exists => TRUE);

-- Raw: 30 days at full resolution (ADD §9.5 item 4, §9.2). Everything older is served by the
-- rollups in 1802. Every continuous-aggregate refresh window is shorter than this, so a
-- refresh never reaches for a chunk retention has already dropped.
SELECT add_retention_policy('telemetry.positions', INTERVAL '30 days', if_not_exists => TRUE);

-- Aggregates: 12 months (ADD §9.5 item 4). Cold export of the aggregates to Parquet is a
-- monthly job, not a schema object.
SELECT add_retention_policy('telemetry.positions_1m',      INTERVAL '12 months', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry.positions_5m',      INTERVAL '12 months', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry.positions_1h',      INTERVAL '12 months', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry.fleet_health_5m',   INTERVAL '12 months', if_not_exists => TRUE);
