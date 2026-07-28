-- =====================================================================================
-- 0710 — dispatch: idempotent command replay log
-- Source: D3' §0 "Idempotency" · ADD §11.13 · R-14
--
-- ⚠ Spec gap — micro-change-set, raised in the C023 handoff. The third instance of the same
--   one: iam (0104, C020), registry (0307, C021), now dispatch. D3' §0 requires an
--   `Idempotency-Key` on every POST mutation and replays a duplicate "from a **per-service**
--   command log"; `dispatch.yaml` declares the header on POST /v1/standby/online, /offline,
--   /v1/standby/directional and POST /v1/rides/job-board/{rideId}/intent. D4' §5 and
--   server_db_schema.md §5 still print DDL for `rides.command_log` only.
--
--   Pointing dispatch-svc at rides.command_log would give two bounded contexts one shared
--   primary key: a driver going online and a passenger booking could collide on an identical
--   client-generated key, and the second caller would be replayed the first one's response.
--   **D4' should print one command-log table per service that has idempotent POSTs** — the
--   convention is recorded in db/CLAUDE.md.
--
--   Shape is 0603 exactly, minus `ride_id`: going on standby targets no ride, and
--   MageRide.Shared's PostgresCommandLog omits the column when CommandLog:AggregateIdColumn is
--   null. The response_body JSON / response_content_type divergences carry over from 0603 for
--   the same reasons (C002 micro-change-set (a)).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.command_log (
  -- The primary key IS the idempotency mechanism: the reservation is a single
  -- INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, so concurrent duplicates are settled
  -- by the index rather than by application locking.
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the driver, or NULL for an internal caller
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reclaiming a reservation abandoned by a dead process scans for rows with no response older
-- than CommandLog:StaleReservationAfter; without this the scan is a seq scan of the log.
CREATE INDEX IF NOT EXISTS ix_dispatch_command_log_inflight
  ON dispatch.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE dispatch.command_log IS
  'R-14 idempotent replay for dispatch-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';
