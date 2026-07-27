-- =====================================================================================
-- 0801 — reputation: rolling counters and effective block state
-- Source: server_db_schema.md §7 · D4' §7 · ADD §9.1 · D5' §7.2 · D-04, US-6A.10b, AL-16
--
-- dispatch-svc reads block_states over gRPC on every candidate build (D-04), so this pair is
-- deliberately one row per user and index-free beyond the PK.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS reputation.counters (
  user_id UUID PRIMARY KEY REFERENCES iam.users(id),
  -- POST-acceptance cancels only; pre-acceptance never counts. 3 consecutive →
  -- BOOKING_DISABLED, and ANY completed ride resets it to 0 (D5' §7.2).
  cancellations_continuous SMALLINT NOT NULL DEFAULT 0
    CONSTRAINT ck_counters_cancellations CHECK (cancellations_continuous >= 0),
  reports_total INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_counters_reports CHECK (reports_total >= 0),
  no_shows INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_counters_no_shows CHECK (no_shows >= 0),
  window_reset_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('reputation','counters');

CREATE TABLE IF NOT EXISTS reputation.block_states (
  user_id UUID PRIMARY KEY REFERENCES iam.users(id),
  state TEXT NOT NULL DEFAULT 'OK' CONSTRAINT ck_block_states_state
    CHECK (state IN ('OK','WARN','BOOKING_DISABLED','DELISTED')),
  expires_at TIMESTAMPTZ,                                     -- NULL = until lifted by hand
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('reputation','block_states');

COMMENT ON TABLE reputation.counters IS
  'Per-user rolling counters (D-04). Covers both roles: cancellations_continuous is the passenger booking-disable input (US-6A.10b); no_shows and reports_total drive driver delisting.';
COMMENT ON TABLE reputation.block_states IS
  'Effective block state, consumed by dispatch-svc over gRPC on every candidate build (D-04). Re-enable after BOOKING_DISABLED requires the outstanding Rs 50 cleared (D5'' §7.2).';
