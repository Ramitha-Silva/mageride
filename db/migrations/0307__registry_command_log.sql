-- =====================================================================================
-- 0307 — registry: idempotent command replay log
-- Source: D3' §0 "Idempotency" · ADD §11.13 · R-14
--
-- ⚠ Spec gap — micro-change-set, raised in the C021 handoff. Same one C020 raised for iam
--   (0104), now confirmed as a pattern rather than a one-off.
--   D3' §0 requires an `Idempotency-Key` on every POST mutation and replays a duplicate "from
--   a **per-service** command log"; the registry-svc contract marks POST /v1/vehicles
--   "Idempotent: yes". D4' §5 and server_db_schema.md §5 print DDL for `rides.command_log`
--   only. Pointing registry-svc at it would give two bounded contexts one shared primary key,
--   so a registration and a ride command could collide on an identical client-generated key.
--   **D4' should print one command-log table per service that has idempotent POSTs.**
--
--   Shape is 0603 exactly, minus `ride_id`: a vehicle registration targets no aggregate that
--   exists yet, and MageRide.Shared's PostgresCommandLog omits the column when
--   CommandLog:AggregateIdColumn is null. The response_body JSON / response_content_type
--   divergences carry over from 0603 for the same reasons (C002 micro-change-set (a)).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.command_log (
  -- The primary key IS the idempotency mechanism: the reservation is a single
  -- INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, so concurrent duplicates are settled
  -- by the index rather than by application locking.
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the driver; every route here is authenticated
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reclaiming a reservation abandoned by a dead process scans for rows with no response older
-- than CommandLog:StaleReservationAfter; without this the scan is a seq scan of the log.
CREATE INDEX IF NOT EXISTS ix_registry_command_log_inflight
  ON registry.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE registry.command_log IS
  'R-14 idempotent replay for registry-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';
