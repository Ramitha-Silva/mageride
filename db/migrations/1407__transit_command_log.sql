-- =====================================================================================
-- 1407 — transit: R-14 command log (C057, transit-svc-gtfs-lifecycle)
-- Source: ADD §9.1 R-14 · D3' §0 "Idempotency" · D4' §5 · BR-32.2
--         backend/contracts/transit.yaml activateGtfsFeed (`Idempotency-Key`)
--
-- ⚠ Spec gap — micro-change-set, raised in the C057 handoff. The eleventh instance of the
--   same one (iam 0104, registry 0307, prov 0402, trips 0505, rides 0603, dispatch 0710,
--   reputation 0803, fares 1005, subscription 1203, content 1307, comms 1308): D4' §5 prints
--   command-log DDL for `rides` only, while D3' §0 makes `Idempotency-Key` mandatory on every
--   POST mutation.  **D4' §5 should carry a command log per bounded context.**
--
-- Shape is 1308 exactly (0603 minus the aggregate-id column). transit-svc has one idempotent
-- POST — `/v1/admin/transit/gtfs/uploads/{id}/activate` — and BR-32.2 names the guarantee out
-- loud: "Idempotent on `Idempotency-Key`". A double-clicked **Activate** in SCR-AP-016 must
-- swap the live dataset once and replay the same 200 the second time, not run a second
-- staging import and a second `NOTIFY` behind an operator's back.
--
-- No aggregate-id column: `MageRide.Shared`'s `PostgresCommandLog` omits it when
-- `CommandLog:AggregateIdColumn` is null, and the middleware never populates one — the
-- feed version the command targets is already in the request path, which the request hash
-- covers.
--
-- The multipart upload (`POST …/uploads`) is deliberately NOT in this table. Its body is up
-- to 200 MB and the kernel's replay hashes and buffers the request body to detect key reuse;
-- and it does not need to be, because that endpoint is **content-addressed**: BR-32.1 dedupes
-- on the file's sha256 (`ux` on `transit.gtfs_feed_versions.sha256`), which is a stronger
-- guarantee than a header — it catches a retry that regenerated its key, and it catches the
-- same file uploaded a month later by a different operator. See the C057 handoff.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS transit.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reservations that never completed: a request that died between reserving the key and
-- writing its response. The middleware takes them over by age.
CREATE INDEX IF NOT EXISTS ix_transit_command_log_inflight
  ON transit.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE transit.command_log IS
  'R-14 idempotent replay for transit-svc''s one idempotent POST — the SCR-AP-016 activation (BR-32.2). 5xx is never stored, so a retry re-executes rather than replaying a failure. The GTFS upload is exempt: it dedupes on the file sha256, not on a header.';
