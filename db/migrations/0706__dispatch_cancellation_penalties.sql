-- =====================================================================================
-- 0706 — dispatch: Rs 50 cross-trip cancellation penalty
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 · D5' §7.1 · D-05, AL-16, US-6A.9
--
-- There is no card on file, so a post-acceptance cancellation is ACCRUED here and COLLECTED
-- on the passenger's next completed trip: Rs 50 is added to that fare and passed through the
-- next driver's wallet to the driver who was stood up (AL-16).
--
-- Column naming: ADD §9.1 prose writes `applied_trip_id`; both DDL sources write
-- `applied_ride_id`, and this is a Mode C ride, so the DDL spelling wins.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.cancellation_penalties (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  -- No FK on either ride reference: an accrued debt must survive the deletion or PDPA erasure
  -- of the ride it came from, and applied_ride_id is set long after that ride is terminal.
  original_ride_id UUID NOT NULL,
  affected_driver_id UUID NOT NULL REFERENCES iam.users(id),
  amount_minor INTEGER NOT NULL DEFAULT 5000                  -- Rs 50, integer minor units
    CONSTRAINT ck_cancellation_penalties_amount CHECK (amount_minor >= 0),
  status TEXT NOT NULL DEFAULT 'OUTSTANDING'
    CONSTRAINT ck_cancellation_penalties_status CHECK (status IN ('OUTSTANDING','SETTLED')),
  applied_ride_id UUID,                                       -- NULL until settled
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- D-05 idempotent cross-trip apply, exactly as both specs print it.
--
-- ⚠ Worth knowing: because `id` is the primary key, the pair (id, applied_ride_id) is unique
--   by construction, so this index rejects nothing on its own. The real double-apply guard is
--   twofold and lives elsewhere: the settlement UPDATE is conditional on
--   status='OUTSTANDING' (a settled penalty cannot be re-settled), and the ledger entry is
--   keyed billing.journal_entries.idempotency_key = penalty_id || ':' || rideId (D5' §7.1,
--   C005). Landed as specified rather than reinterpreted — see the C004 handoff note.
CREATE UNIQUE INDEX IF NOT EXISTS ux_penalty_apply
  ON dispatch.cancellation_penalties(id, applied_ride_id);

-- The settlement path is "every OUTSTANDING penalty for this passenger, FOR UPDATE SKIP
-- LOCKED" on each completed trip (D5' §7.1).
CREATE INDEX IF NOT EXISTS ix_penalty_outstanding
  ON dispatch.cancellation_penalties(passenger_id) WHERE status = 'OUTSTANDING';

COMMENT ON TABLE dispatch.cancellation_penalties IS
  'Rs 50 cross-trip cancellation penalty (D-05, AL-16). Accrued on cancel-after-accept, collected on the next completed trip; the next driver is a pass-through, not the beneficiary.';
