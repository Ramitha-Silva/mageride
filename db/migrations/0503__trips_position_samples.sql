-- =====================================================================================
-- 0503 — trips: 1/min operational position samples, monthly range partitions
-- Source: server_db_schema.md §4, §9.2/§21 · D4' §4 · ADD §9.1/§9.2
--
-- Operational Mode A/B history only. High-frequency hardware telemetry goes to
-- telemetry.positions (C006, TimescaleDB hypertable), never here — §21 "high-frequency raw
-- GPS never lands in Postgres operational tables".
--
-- Retention (§9.2): 12 months hot, then cold archive (MinIO/Wasabi). Detaching a monthly
-- partition is the archive step, which is why this is range-partitioned rather than a plain
-- table with a DELETE job.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS trips.position_samples (
  id BIGINT GENERATED ALWAYS AS IDENTITY,
  -- No FKs: both specs print the columns bare. A partitioned high-volume write path pays an
  -- index probe per row per constraint, and the writer (persistence-writer-svc, C040) already
  -- holds the session it is writing for.
  session_id UUID NOT NULL,
  vehicle_id UUID NOT NULL,
  geo GEOGRAPHY(POINT,4326) NOT NULL,
  speed_mps REAL,
  heading_deg SMALLINT,
  sample_ts TIMESTAMPTZ NOT NULL,
  -- The partition key must be part of every unique constraint, hence the composite PK.
  PRIMARY KEY (id, sample_ts)
) PARTITION BY RANGE (sample_ts);

-- Created on the parent, so every existing and future partition inherits it.
CREATE INDEX IF NOT EXISTS ix_possample_session
  ON trips.position_samples(session_id, sample_ts DESC);

-- Partition boundaries are Asia/Colombo month starts (D-38). Passing DATE literals straight
-- into FOR VALUES would resolve them against whatever TimeZone the migration session happens
-- to carry, so the bounds are computed as explicit TIMESTAMPTZ values instead.
CREATE OR REPLACE FUNCTION trips.ensure_position_samples_partition(p_month DATE)
RETURNS TEXT AS $$
DECLARE
  v_start TIMESTAMP   := date_trunc('month', p_month::timestamp);
  v_from  TIMESTAMPTZ := v_start AT TIME ZONE 'Asia/Colombo';
  v_to    TIMESTAMPTZ := (v_start + INTERVAL '1 month') AT TIME ZONE 'Asia/Colombo';
  v_name  TEXT        := 'position_samples_' || to_char(v_start, 'YYYY_MM');
BEGIN
  EXECUTE format(
    'CREATE TABLE IF NOT EXISTS trips.%I PARTITION OF trips.position_samples '
    'FOR VALUES FROM (%L) TO (%L)', v_name, v_from, v_to);
  RETURN v_name;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION trips.ensure_position_samples_partition(DATE) IS
  'Creates the monthly partition covering p_month if it does not exist (§9.2). Called by this migration for a rolling window and by the maintenance job thereafter.';

-- A rolling window: last month through the next twelve. There is deliberately NO DEFAULT
-- partition — a default that has accumulated out-of-range rows blocks CREATE ... PARTITION OF
-- for the month those rows belong to, which turns a missed maintenance run into an outage
-- during recovery rather than at the moment of the write.
DO $$
DECLARE
  v_base DATE := date_trunc('month', (now() AT TIME ZONE 'Asia/Colombo'))::date;
  m INT;
BEGIN
  FOR m IN -1..12 LOOP
    PERFORM trips.ensure_position_samples_partition((v_base + (m || ' months')::interval)::date);
  END LOOP;
END $$;

COMMENT ON TABLE trips.position_samples IS
  'Operational 1/min Mode A/B position history, monthly range partitions (§9.2). High-frequency tracker telemetry belongs in telemetry.positions, not here.';
