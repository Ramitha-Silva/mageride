-- =====================================================================================
-- 0506 — trips: the 1/min operational sample's dedupe key, and the trip summary
-- Source: ADD §9.2, §9.5 item 2 · server_db_schema.md §21 · C040 persistence-writer-svc
--
-- Both objects here exist because ADD §9.2 names a durable artefact that no DDL source
-- prints:
--
--   "High-frequency raw position data does not go to Postgres. Only 1/min sampled +
--    trip summary (start, end, distance, polyline) are persisted operationally."
--
-- `trips.position_samples` (0503) is the 1/min half and has existed since C004 with no
-- writer — C031 handed it to persistence-writer-svc explicitly. The **trip summary** has
-- no table, no columns and no owner anywhere in D4', server_db_schema.md or the ADD; only
-- that one sentence and ADD §9.5 item 2's remark that the *query* path for it "hits
-- aggregates, not raw rows". A continuous aggregate cannot answer "start, end, distance,
-- polyline" for one journey — it is bucketed by time and knows nothing about sessions —
-- so the artefact §9.2 promises has to be stored. Raised as a micro-change-set in the
-- C040 handoff: **D4' §4 should carry `trips.session_summaries`.**
-- =====================================================================================

-- --- 1. The 1/min sample's idempotency key -------------------------------------------
-- ⚠ 0503 gives the table `PRIMARY KEY (id, sample_ts)` over a generated identity, so
--   every write is unconditionally a new row. A 1/min downsample driven by an
--   at-least-once Kafka consumer (D6' §2.3) will re-see the same minute after any
--   rebalance or restart, and without a key each redelivery appends a duplicate.
--
--   The writer therefore stores each row at its **minute boundary** — `sample_ts` is the
--   minute the sample represents, not the instant the fix was taken, which is what a
--   1/min series means — and this index makes the insert idempotent by construction. No
--   in-memory "last written minute per vehicle" is needed, so two replicas and a restart
--   all converge on the same rows.
--
--   `sample_ts` has to be in the key regardless: it is the range partition column, and
--   Postgres rejects a unique constraint on a partitioned table that omits one.
CREATE UNIQUE INDEX IF NOT EXISTS ux_possample_session_minute
  ON trips.position_samples(session_id, sample_ts);

COMMENT ON INDEX trips.ux_possample_session_minute IS
  'One operational sample per session per minute (ADD §9.2). sample_ts is the minute boundary the row represents, which is what makes an at-least-once writer idempotent without per-vehicle state; it is also the partition key, which every unique constraint on a partitioned table must contain.';

-- --- 2. The trip summary (ADD §9.2, §9.5 item 2) --------------------------------------
-- A table of its own rather than columns on `trips.sessions`, for two reasons.
--
--   (a) `trips.sessions` is trip-state-svc's aggregate and it is that service's alone to
--       write — it carries the D-03 mutex, the state machine and an `updated_at` trigger.
--       A second service writing into the same row would make "who last changed this
--       session" unanswerable in a support timeline.
--   (b) US-5.10 lets an auto-ended session be **restarted** inside a five-minute grace,
--       in place, keeping its id. A summary is therefore not a final fact about a row: it
--       has to be replaceable when the journey it described resumes and then ends again.
--       A separate row upserted on `session.ended` handles that; a column set written once
--       would either be stale or would need the state machine to know about summaries.
CREATE TABLE IF NOT EXISTS trips.session_summaries (
  -- One summary per session. No FK to trips.sessions: this is written by a consumer of
  -- `trip.events` (D6' §2.3, at-least-once) and an FK would turn a summary that arrived
  -- before its session's commit was visible on a replica into a retry storm rather than a
  -- row. The session id comes off the event, which trip-state-svc only emits post-commit.
  session_id UUID PRIMARY KEY,
  vehicle_id UUID NOT NULL,
  driver_id  UUID NOT NULL,
  mode CHAR(1) NOT NULL CONSTRAINT ck_summaries_mode CHECK (mode IN ('A','B')),

  started_at TIMESTAMPTZ NOT NULL,
  ended_at   TIMESTAMPTZ NOT NULL,
  end_reason TEXT,

  -- ADD §9.2's four named fields. All four are nullable-in-effect: a session that ended
  -- with no fixes at all — a driver who started and immediately stopped, or a tracker that
  -- never reported — is a real outcome and gets a summary saying so rather than no row.
  start_geo GEOGRAPHY(POINT,4326),
  end_geo   GEOGRAPHY(POINT,4326),
  distance_m DOUBLE PRECISION NOT NULL DEFAULT 0
    CONSTRAINT ck_summaries_distance CHECK (distance_m >= 0),
  -- A LINESTRING needs two distinct points, so a single-fix journey stores NULL here and
  -- still carries its distance (0) and its start/end.
  polyline GEOGRAPHY(LINESTRING,4326),

  sample_count  INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_summaries_samples CHECK (sample_count >= 0),
  max_speed_mps REAL,
  avg_speed_mps REAL,

  -- Which relation the geometry was computed from, because the two differ in accuracy by
  -- an order of magnitude and a reader comparing two journeys must be able to tell:
  --   'telemetry'   — full-resolution telemetry.positions (a fix every 2–10 s, D5' §5.2)
  --   'operational' — the 1/min trips.position_samples, a lower bound on distance
  --   'none'        — the session produced no fixes
  -- ADD §9.5 item 6 names "trip linestring for trip Y" as a raw-chunk read, which is the
  -- 'telemetry' path; the fallback exists because telemetry.positions is dropped after 30
  -- days (§9.5 item 4) while a summary is kept for 12 months with the samples it can be
  -- rebuilt from.
  geometry_source TEXT NOT NULL DEFAULT 'none'
    CONSTRAINT ck_summaries_geometry_source
    CHECK (geometry_source IN ('telemetry','operational','none')),

  computed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT now());

-- "every journey this vehicle made", and the fleet-portal history read.
CREATE INDEX IF NOT EXISTS ix_summaries_vehicle
  ON trips.session_summaries(vehicle_id, ended_at DESC);

-- "every journey this driver made" — the driver-app history list and the payout report.
CREATE INDEX IF NOT EXISTS ix_summaries_driver
  ON trips.session_summaries(driver_id, ended_at DESC);

SELECT public.attach_set_updated_at('trips','session_summaries');

COMMENT ON TABLE trips.session_summaries IS
  'Per-session trip summary — start, end, distance, polyline (ADD §9.2). Written by persistence-writer-svc (C040) on session.ended, upserted because US-5.10 lets a session restart and end again. NOT printed by D4'' §4 or server_db_schema.md; micro-change-set in the C040 handoff.';
COMMENT ON COLUMN trips.session_summaries.distance_m IS
  'Path length over the ground, in metres. Computed from telemetry.positions when the raw chunks still hold the journey (accurate), else from the 1/min operational samples (a lower bound). geometry_source says which.';
COMMENT ON COLUMN trips.session_summaries.polyline IS
  'The journey as a line, simplified to PersistenceWriter:PolylineToleranceM so a map render is cheap. NULL when the session produced fewer than two distinct fixes.';
COMMENT ON COLUMN trips.session_summaries.geometry_source IS
  'telemetry = full-resolution telemetry.positions; operational = the 1/min trips.position_samples, which underestimates distance; none = the session produced no fixes at all.';
