-- =====================================================================================
-- 1005 — fares: R-14 command log
-- Source: ADD §9.1 R-14 · D3' §0 "Idempotency" · D4' §5
--
-- ⚠ Spec gap — micro-change-set, raised in the C049 handoff.
--   R-14 requires a per-service command log so a retried POST replays its original response
--   instead of re-executing, and D3' §0 makes `Idempotency-Key` mandatory on every POST
--   mutation — `POST /v1/fare/calculate` carries one in `fare.yaml`. D4' §5 prints DDL for
--   `rides.command_log` only: the same gap C020, C021, C030, C033, C034, C045, C046 and C047
--   each raised for their own bounded context, and fare-svc is the ninth.
--
--   Shape is 1203 exactly (0307 minus the aggregate id). fare-svc's POSTs target no aggregate
--   this service owns: a calculation names a ride, and `rides.rides` is ride-svc's (R-01).
--
--   `billing.command_log` is NOT reused, for the reason 1203 records: it is wallet-svc's, its
--   primary key is the bare idempotency key, and two services sharing it would let a client's
--   key collide across a service boundary and be served the wrong response body.
--
--   **D4' §5 should carry a command log per bounded context.**
--
-- The replay log is the *second* guard on a calculation, not the first. What makes
-- `POST /v1/fare/calculate` single-shot is a `FOR UPDATE` read of the ride's row in
-- `fares.ride_payments` inside the writing transaction: a header dedupes identical *requests*,
-- and what must never happen twice is a *ride* being priced — two different keys for one ride
-- would otherwise leave a passenger holding two fares. §9 cannot express that as a unique index,
-- because D-10's retry chain deliberately puts several attempts on one ride.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS fares.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reservations that never completed: a request that died between reserving the key and writing
-- its response. The middleware sweeps them by age.
CREATE INDEX IF NOT EXISTS ix_fares_command_log_inflight
  ON fares.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE fares.command_log IS
  'R-14 idempotent replay for fare-svc''s POST mutations (D3'' §0). 5xx is never stored, so a retry re-executes rather than replaying a failure. The load-bearing guard on a fare is the FOR UPDATE on fares.ride_payments, not this table.';

-- -------------------------------------------------------------------------------------
-- The idempotency-key spelling this service composes, recorded beside 1101's, 1107's and
-- 1203's. It is a business fact and never a random value, so a retry collides in wallet-svc's
-- ledger instead of paying a cancellation penalty twice:
--
--   penalty settle   penalty_id || ':' || ride_id            (D5' §7.1, exactly)
--
-- fare-svc composes it; wallet-svc's UNIQUE billing.journal_entries.idempotency_key enforces
-- it. 1101's header fixes the same spelling from the other side, and D5' §7.1 prints it as
-- `concat(penalty_id,':',tripId)` — the two must stay identical or the penalty is paid twice.
-- -------------------------------------------------------------------------------------
