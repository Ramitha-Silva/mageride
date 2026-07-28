-- =====================================================================================
-- 0402 — prov: idempotent command replay log
-- Source: D3' §0 "Idempotency" · ADD §11.13 · R-14
--
-- ⚠ Spec gap — micro-change-set, raised in the C030 handoff. The third instance of the one
--   C020 raised for iam (0104) and C021 for registry (0307), so it is settled as a pattern.
--   D3' marks `POST /v1/trackers/bind` and `POST /v1/fleets/{id}/trackers/bulk` "Idempotency-Key
--   required · Idempotent: yes"; D4' §5 prints DDL for `rides.command_log` alone. Sharing that
--   table would give two bounded contexts one primary key, so a tracker bind and a ride command
--   could collide on an identical client-generated key.
--
--   Shape is 0307 exactly (0603 minus `ride_id`): a bind targets a binding that does not exist
--   yet, and MageRide.Shared's PostgresCommandLog omits the column when
--   CommandLog:AggregateIdColumn is null.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS prov.command_log (
  -- The primary key IS the idempotency mechanism: the reservation is a single
  -- INSERT ... ON CONFLICT (idempotency_key) DO NOTHING, so concurrent duplicates are settled
  -- by the index rather than by application locking. It is also what keeps a retried bind from
  -- reaching the anti-clone check and quarantining the caller's own binding (T-08).
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the vehicle owner or fleet admin
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_prov_command_log_inflight
  ON prov.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE prov.command_log IS
  'R-14 idempotent replay for provisioning-svc''s POST mutations (D3'' §0). A replayed bind returns the original credential rather than minting a second one.';
