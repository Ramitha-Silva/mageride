-- =====================================================================================
-- 0804 — reputation: block-state provenance and the fraud-flag lifecycle
-- Source: D3' reputation-svc `FraudFlag` schema · D5' §4.2/§7.2 · ADD §12.6 · D-04, E-07, AL-16
--
-- ⚠ MICRO-CHANGE-SET, raised in the C033 handoff. Two rules that are spec'd have nowhere to
--   live in the 0801/0802 DDL:
--
--   (a) **A manual override has to survive the next automatic recompute.** D3' gives
--       reputation-svc `POST /v1/admin/drivers/{id}/level/restore` for an appeal and this
--       component's deliverable list asks for "admin surfaces for manual state override with
--       audit". `reputation.block_states` records the state and not who decided it, so an
--       admin lifting a block would be undone by the next report the detector counted.
--       `source` is what makes the override stick; `reason` is what the admin UI shows.
--
--   (b) **A flag has a lifecycle.** `reputation.yaml`'s `FraudFlag` types `status` as
--       `open | dismissed | actioned` and `GET /v1/admin/reputation/flags` filters on it, but
--       0802 has no such column — so nothing could ever leave the review queue. `subject_type`
--       is on the same contract schema for the same reason.
--
--   (c) **"Exactly once per detection window" needs a key, not a convention.** E-07 runs on a
--       schedule over a rolling 30-day window; without `window_key` in a unique index, every
--       pass re-raises the same collusion pair and the admin queue fills with the same fact.
-- =====================================================================================

ALTER TABLE reputation.block_states
  -- 'auto'   — derived from reputation.counters by the D5' rules; recomputed on every intake.
  -- 'manual' — an admin decision. Never overwritten by a recompute; only another admin
  --            decision, or `expires_at` passing, returns the row to automatic control.
  ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'auto';
ALTER TABLE reputation.block_states
  -- Which rule set the state: cancellations_disabled | reports_delist | driver_cancel_delist |
  -- no_show_warn | manual | cleared. Read on expiry to decide what to forgive — a time-boxed
  -- delisting that expired has been served, so the counter that caused it is reset with it.
  ADD COLUMN IF NOT EXISTS reason TEXT;
ALTER TABLE reputation.block_states
  ADD COLUMN IF NOT EXISTS set_by UUID REFERENCES iam.users(id);  -- NULL for an automatic move

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'reputation.block_states'::regclass
                    AND conname = 'ck_block_states_source') THEN
    ALTER TABLE reputation.block_states
      ADD CONSTRAINT ck_block_states_source CHECK (source IN ('auto','manual'));
  END IF;
END $$;

-- The expiry sweep claims rows whose time-box has passed; without this it scans every user
-- the platform has ever blocked.
CREATE INDEX IF NOT EXISTS ix_block_states_expiring
  ON reputation.block_states(expires_at) WHERE expires_at IS NOT NULL;

COMMENT ON COLUMN reputation.block_states.source IS
  'auto = derived from reputation.counters by the D5'' rules; manual = an admin decision that a recompute may not overwrite (audited to audit.events, D-35).';
COMMENT ON COLUMN reputation.block_states.expires_at IS
  'When a time-boxed state lapses. NULL = until a rule or an admin lifts it. D5'' §4.2 makes the 3-report delisting "temporary"; AL-16 makes the booking-disable re-enable "after a configurable cooldown or admin/CSR reinstatement".';

-- -------------------------------------------------------------------------------------
-- reputation.counters — the rolling window (D-04 "rolling-window reset")
-- -------------------------------------------------------------------------------------

COMMENT ON COLUMN reputation.counters.window_reset_at IS
  'Start of the current rolling window — the instant reports_total and no_shows were last cleared. The window ends at window_reset_at + Reputation:CounterWindow (30 d). cancellations_continuous is NOT window-scoped: it is a consecutive run, reset by any completed ride (D5'' §7.2).';

-- -------------------------------------------------------------------------------------
-- reputation.fraud_flags — the E-07 review queue
-- -------------------------------------------------------------------------------------

ALTER TABLE reputation.fraud_flags
  ADD COLUMN IF NOT EXISTS subject_type TEXT;
ALTER TABLE reputation.fraud_flags
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'open';
ALTER TABLE reputation.fraud_flags
  -- The detection window this flag belongs to, e.g. '2026-W30' or '2026-07-28'. Part of the
  -- uniqueness key: the same pair detected in a later window is a new fact, not a duplicate.
  ADD COLUMN IF NOT EXISTS window_key TEXT NOT NULL DEFAULT '';
ALTER TABLE reputation.fraud_flags
  ADD COLUMN IF NOT EXISTS resolved_by UUID REFERENCES iam.users(id);
ALTER TABLE reputation.fraud_flags
  ADD COLUMN IF NOT EXISTS resolved_at TIMESTAMPTZ;
ALTER TABLE reputation.fraud_flags
  ADD COLUMN IF NOT EXISTS resolution_note TEXT;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'reputation.fraud_flags'::regclass
                    AND conname = 'ck_fraud_flags_status') THEN
    ALTER TABLE reputation.fraud_flags
      ADD CONSTRAINT ck_fraud_flags_status CHECK (status IN ('open','dismissed','actioned'));
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'reputation.fraud_flags'::regclass
                    AND conname = 'ck_fraud_flags_subject_type') THEN
    ALTER TABLE reputation.fraud_flags
      ADD CONSTRAINT ck_fraud_flags_subject_type
      CHECK (subject_type IS NULL OR subject_type IN ('driver','passenger','vehicle'));
  END IF;
END $$;

-- One flag per (kind, subject, counterparty, window) — this component's DoD: "a synthetic
-- collusion pattern raises fraud.suspected exactly once per detection window". NULLS NOT
-- DISTINCT (PG 15+) is load-bearing: a single-subject detector such as the device-binding
-- cross-check leaves related_id NULL, and the default NULLS DISTINCT would let every pass
-- insert another copy of it.
CREATE UNIQUE INDEX IF NOT EXISTS ux_fraud_flags_window
  ON reputation.fraud_flags(kind, subject_id, related_id, window_key) NULLS NOT DISTINCT;

-- The admin queue's default view: open flags, newest first.
CREATE INDEX IF NOT EXISTS ix_fraud_flags_status
  ON reputation.fraud_flags(status, ts DESC);

COMMENT ON COLUMN reputation.fraud_flags.status IS
  'Review state (reputation.yaml FraudFlag). A flag never blocks anybody by itself — an admin actions it, which is a separate block-state override.';
COMMENT ON COLUMN reputation.fraud_flags.window_key IS
  'The detection window this signal was raised in. With ux_fraud_flags_window this is what makes a repeated detection idempotent instead of a duplicate queue entry.';
