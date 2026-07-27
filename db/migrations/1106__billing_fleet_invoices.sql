-- =====================================================================================
-- 1106 — billing: monthly fleet invoices
-- Source: server_db_schema.md §10 · D4' §10 · ADD §9.1 · AL-03, D-38
--
-- One invoice per fleet per Asia/Colombo month: Σ of the per-Mode-B-vehicle platform fee
-- (billing.monthly_subscriptions) for that fleet's vehicles. Mode A vehicles are free and
-- contribute nothing.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.fleet_invoices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  period_month DATE NOT NULL,
  period_month_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),      -- D-38 audit companion
  total_minor INTEGER NOT NULL CHECK (total_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  status TEXT NOT NULL DEFAULT 'DUE' CONSTRAINT ck_fleet_invoices_status
    CHECK (status IN ('FREE','DUE','PAID')),
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_fleet_invoices_fleet_period UNIQUE (fleet_id, period_month),
  CONSTRAINT ck_fleet_invoices_period_first_day
    CHECK (period_month = date_trunc('month', period_month)::date),
  -- A fleet whose vehicles are all Mode A owes nothing, and a FREE invoice must not post.
  CONSTRAINT ck_fleet_invoices_free
    CHECK (status <> 'FREE' OR (total_minor = 0 AND journal_entry_id IS NULL)));

CREATE INDEX IF NOT EXISTS ix_fleet_invoices_due
  ON billing.fleet_invoices(period_month) WHERE status = 'DUE';

COMMENT ON TABLE billing.fleet_invoices IS
  'Monthly fleet billing (AL-03): Σ per-Mode-B-vehicle platform fee for one fleet. Mode A is free, so a Mode-A-only fleet gets a FREE invoice rather than no invoice — the row is the evidence the run considered them.';
