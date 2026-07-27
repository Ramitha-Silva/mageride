-- =====================================================================================
-- 1004 — fares: daily driver earnings aggregate
-- Source: server_db_schema.md §9 · D4' §9 · ADD §9.1 · D-38
--
-- Read model behind the driver Earnings screen (SCR-DA-021). The ledger remains the
-- master of money — this is a per-day rollup so the app does not aggregate journal
-- postings on every open.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS fares.driver_earnings (
  driver_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  earn_date DATE NOT NULL,                                    -- Asia/Colombo (D-38)
  -- D-38 audit companion: the instant earn_date was derived from, so the business-day
  -- boundary a row landed on stays auditable. Same convention as
  -- dispatch.directional_filters.used_date_tz_at (C004).
  earn_date_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  trips INTEGER NOT NULL DEFAULT 0 CHECK (trips >= 0),
  gross_minor INTEGER NOT NULL DEFAULT 0 CHECK (gross_minor >= 0),
  daily_fee_minor INTEGER NOT NULL DEFAULT 0 CHECK (daily_fee_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  PRIMARY KEY (driver_id, earn_date));

COMMENT ON TABLE fares.driver_earnings IS
  'Per-driver per-day earnings rollup in Asia/Colombo (D-38). Derived from the ledger, never the source of truth for a balance.';
COMMENT ON COLUMN fares.driver_earnings.earn_date_tz_at IS
  'D-38 audit companion: the exact instant earn_date was derived from.';
COMMENT ON COLUMN fares.driver_earnings.daily_fee_minor IS
  'The D-13 daily fee attributed to this day. Stored alongside gross so the app can show net without a second query; billing.daily_fee_charges stays the charge record.';
