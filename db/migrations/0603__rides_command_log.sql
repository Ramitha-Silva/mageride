-- =====================================================================================
-- 0603 — rides: idempotent command replay log
-- Source: server_db_schema.md §5 · D4' §5 · ADD §9.1/§11.13 · D5' §6.2 · R-14, R-18
--
-- ⚠ Two columns diverge from the printed DDL. Both were raised as micro-change-set (a) in the
--   C002 handoff and are actioned here, because C004 owns this table's DDL:
--
--   1. response_body is JSON, not JSONB. ADD §11.13 requires a replay to return the stored
--      response *verbatim*. jsonb is a parsed representation — it drops insignificant
--      whitespace, discards duplicate keys and reorders object members, so a jsonb replay is
--      semantically equal but not byte for byte. Postgres `json` keeps the exact input text
--      and is still queryable with the JSON operators.
--   2. response_content_type is new. Without it a replay cannot tell application/json from
--      application/problem+json, and every error response must stay problem+json.
--
--   MageRide.Shared's PostgresCommandLog defaults to exactly this shape
--   (CommandLogOptions.BodyStorage=Json, ContentTypeColumn=response_content_type); the
--   spec's jsonb form is still selectable via CommandLog:BodyStorage for compatibility.
--   specs/D4_mageride_data_model.md §5 and specs/server_db_schema.md §5 need the same edit.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.command_log (
  -- The primary key IS the idempotency mechanism: the reservation is a single
  -- INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, so concurrent duplicates are settled
  -- by the index rather than by application locking.
  idempotency_key TEXT PRIMARY KEY,
  -- No FK to rides.rides: POST /v1/rides/request reserves its key *before* the ride exists,
  -- and a released reservation must be able to outlive a rolled-back aggregate.
  ride_id UUID,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 422
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- see the header note (was JSONB)
  response_content_type TEXT,                                 -- see the header note (new)
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reclaiming a reservation abandoned by a dead process scans for rows with no response older
-- than CommandLog:StaleReservationAfter; without this the scan is a seq scan of the log.
CREATE INDEX IF NOT EXISTS ix_command_log_inflight
  ON rides.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE rides.command_log IS
  'R-14 idempotent replay for every mutating ride command (D5'' §6.2). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';
COMMENT ON COLUMN rides.command_log.response_body IS
  'json, not jsonb: R-14 replay must be byte for byte (C002 micro-change-set (a)).';
COMMENT ON COLUMN rides.command_log.response_content_type IS
  'Original Content-Type, so a replayed error stays application/problem+json (C002 micro-change-set (a)).';
