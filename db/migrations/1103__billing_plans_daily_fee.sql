-- =====================================================================================
-- 1103 — billing: daily-fee plans and charges
-- Source: server_db_schema.md §10 · D4' §10 · ADD §9.1 · D-13, D-38
--
-- D-13: one flat charge per driver per vehicle per Asia/Colombo day, first trip free.
-- The composite PRIMARY KEY *is* the idempotency mechanism — the charge is an upsert, so
-- a retried "go online" or a redelivered trip-completion event cannot double-charge.
-- Seed rows are in 1901.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.plans (
  vehicle_type TEXT PRIMARY KEY,
  daily_fee_minor INTEGER NOT NULL CHECK (daily_fee_minor >= 0),
  mode CHAR(1) NOT NULL CONSTRAINT ck_plans_mode CHECK (mode IN ('A','B','C')),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('billing','plans');

COMMENT ON TABLE billing.plans IS
  'Daily-fee rate per vehicle type (ADD §9.1: seven tiers, Mode A free). Admin-editable in Admin Portal Config (SCR-AP-007). truck / mini_truck carry no seeded default — §20 leaves package-delivery rates to be configured before such a vehicle goes online.';

CREATE TABLE IF NOT EXISTS billing.daily_fee_charges (
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  fee_date DATE NOT NULL                                      -- Asia/Colombo (D-13/D-38)
    DEFAULT ((now() AT TIME ZONE 'Asia/Colombo')::date),
  fee_date_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),          -- D-38 audit companion
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  trips_that_day INTEGER NOT NULL DEFAULT 0 CHECK (trips_that_day >= 0),
  status TEXT NOT NULL DEFAULT 'PAID' CONSTRAINT ck_daily_fee_charges_status
    CHECK (status IN ('PAID','WAIVED_FIRST_TRIP')),
  charged_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (driver_id, vehicle_id, fee_date),
  -- "First trip free" (D-13) means a waived row moved no money. Neither spec prints the
  -- constraint, but a waived charge carrying an amount would be double-counted by every
  -- revenue rollup that sums this table.
  CONSTRAINT ck_daily_fee_charges_waiver
    CHECK (status <> 'WAIVED_FIRST_TRIP' OR amount_minor = 0));

COMMENT ON TABLE billing.daily_fee_charges IS
  'D-13 idempotent daily fee. The (driver_id, vehicle_id, fee_date) PK is the idempotency key: subscription-svc upserts, so a duplicate event is a no-op.';
COMMENT ON COLUMN billing.daily_fee_charges.fee_date IS
  'Asia/Colombo business date (D-13/D-38). Defaults to the Colombo date rather than the session date so a UTC-clocked caller cannot straddle the day boundary.';
COMMENT ON COLUMN billing.daily_fee_charges.status IS
  'WAIVED_FIRST_TRIP records the D-13 free first trip explicitly, so "no row" keeps meaning "not charged yet today" rather than "charged nothing".';
COMMENT ON COLUMN billing.daily_fee_charges.charged_at IS
  'No journal_entry_id column: neither spec prints one and the link is derivable — the ledger entry is billing.journal_entries.idempotency_key = ''daily_fee:'' || driver_id || '':'' || vehicle_id || '':'' || fee_date.';
