-- =====================================================================================
-- 0104 — iam: idempotent command replay log
-- Source: D3' §0 "Idempotency" · ADD §11.13 · R-14, R-18
--
-- ⚠ Spec gap — micro-change-set, raised in the C020 handoff.
--   D3' §0 requires an `Idempotency-Key` on every POST mutation and says duplicates "replay the
--   original response from a **per-service** command log"; the iam-svc contract makes
--   POST /v1/auth/otp/verify idempotent ("Idempotent: yes (replay token)"). But D4' §5 and
--   server_db_schema.md §5 print the DDL for `rides.command_log` only, so no other bounded
--   context has the table its own contract requires. Pointing iam-svc at `rides.command_log`
--   would give two services one shared primary key across a context boundary, so iam gets its
--   own — D4' should print one per service that has idempotent POSTs.
--
--   Shape is 0603 exactly, minus `ride_id`: an auth command targets no aggregate, and
--   MageRide.Shared's PostgresCommandLog omits the column when CommandLog:AggregateIdColumn
--   is null. The response_body JSON / response_content_type divergences carry over from 0603
--   for the same reasons (C002 micro-change-set (a)).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS iam.command_log (
  -- The primary key IS the idempotency mechanism: the reservation is a single
  -- INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, so concurrent duplicates are settled
  -- by the index rather than by application locking.
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,                                   -- 'anonymous' on the public OTP routes
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reclaiming a reservation abandoned by a dead process scans for rows with no response older
-- than CommandLog:StaleReservationAfter; without this the scan is a seq scan of the log.
CREATE INDEX IF NOT EXISTS ix_iam_command_log_inflight
  ON iam.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE iam.command_log IS
  'R-14 idempotent replay for iam-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';
COMMENT ON COLUMN iam.command_log.actor_id IS
  'NULL on the public auth routes — the caller has no session yet, which is the point of the call.';
