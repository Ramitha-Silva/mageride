-- =====================================================================================
-- 1203 — subscription: R-14 command log
-- Source: ADD §9.1 R-14 · D3' §0 "Idempotency" · D4' §5
--
-- ⚠ Spec gap — micro-change-set, raised in the C047 handoff.
--   R-14 requires a per-service command log so a retried POST replays its original response
--   instead of re-executing, and D3' §0 makes `Idempotency-Key` mandatory on every POST
--   mutation. D4' §5 prints DDL for `rides.command_log` only — the same gap C020, C021, C030,
--   C033, C034, C045 and C046 each raised for their own bounded context, and subscription-svc
--   is the eighth.
--
--   Shape is 1107 exactly (0307 minus the aggregate id). subscription-svc's POSTs target no
--   aggregate that exists before the call: the fee-refund intake creates a support ticket, and
--   the charge and the Mode B run are keyed by a Colombo day and a Colombo month rather than by
--   a row.
--
--   `billing.command_log` is NOT reused. It is wallet-svc's table and its primary key is the
--   bare idempotency key: two services sharing it would let a client's key collide across
--   service boundaries, and the loser would be served the other service's response body.
--
--   **D4' §5 should carry a command log per bounded context.**
--
-- The two internal fee routes are deliberately OUTSIDE this table (`AllowMissingIdempotencyKey`).
-- Their key is the business fact — `billing.daily_fee_charges`'s (driver_id, vehicle_id,
-- fee_date) primary key and `billing.journal_entries.idempotency_key` — and a header-based guard
-- over the same money would be weaker: it dedupes identical *requests* rather than identical
-- *days*, so two differently-keyed calls for one Colombo day would both pass it.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS subscription.command_log (
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
CREATE INDEX IF NOT EXISTS ix_subscription_command_log_inflight
  ON subscription.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE subscription.command_log IS
  'R-14 idempotent replay for subscription-svc''s POST mutations (D3'' §0). 5xx is never stored, so a retry re-executes rather than replaying a failure. The internal fee routes are exempt — their idempotency key is the Asia/Colombo day itself (D-13).';

-- -------------------------------------------------------------------------------------
-- The idempotency-key spelling this service composes, recorded beside 1101's and 1107's.
-- It is a business fact and never a random value, so a retry collides in wallet-svc's ledger
-- instead of taking a second day's fee:
--
--   daily fee   'daily_fee:' || driver_id || ':' || vehicle_id || ':' || to_char(fee_date,'YYYY-MM-DD')
--
-- subscription-svc composes it; wallet-svc's UNIQUE billing.journal_entries.idempotency_key
-- enforces it. 1101's header fixes the same spelling from the other side.
-- -------------------------------------------------------------------------------------
