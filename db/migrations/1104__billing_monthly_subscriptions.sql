-- =====================================================================================
-- 1104 — billing: Mode B platform monthly fee
-- Source: server_db_schema.md §10 · D4' §10 · ADD §9.1 · AL-03, D-38
--
-- The PLATFORM's per-Mode-B-vehicle charge (~Rs 300/month, first month free), billed TO
-- the fleet or owner. Not to be confused with subscription.payments (1202), which is the
-- subscriber-facing fare a passenger pays TO the fleet owner. §18b is explicit that the
-- two never net against each other: this one is ledgered, that one is pass-through.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.monthly_subscriptions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  -- First day of the Asia/Colombo billing month (D-38).
  period_month DATE NOT NULL,
  period_month_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),      -- D-38 audit companion
  amount_minor INTEGER NOT NULL DEFAULT 30000 CHECK (amount_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  status TEXT NOT NULL DEFAULT 'DUE' CONSTRAINT ck_monthly_subscriptions_status
    CHECK (status IN ('FREE','DUE','PAID')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_monthly_subscriptions_vehicle_period UNIQUE (vehicle_id, period_month),
  -- period_month names a month, so it must be its first day — otherwise the UNIQUE above
  -- admits two rows for the same month and the "first month free" rule can be re-claimed.
  CONSTRAINT ck_monthly_subscriptions_period_first_day
    CHECK (period_month = date_trunc('month', period_month)::date),
  -- "First month free" (§20): a FREE period charges nothing.
  CONSTRAINT ck_monthly_subscriptions_free CHECK (status <> 'FREE' OR amount_minor = 0));

-- The monthly billing run scans everything still owed.
CREATE INDEX IF NOT EXISTS ix_monthly_subs_due
  ON billing.monthly_subscriptions(period_month) WHERE status = 'DUE';

COMMENT ON TABLE billing.monthly_subscriptions IS
  'Platform Mode B fee charged to the fleet/owner per vehicle (~Rs 300, first month free). Distinct money flow from subscription.payments, which is the subscriber-facing fare (§18b).';
COMMENT ON COLUMN billing.monthly_subscriptions.period_month IS
  'First day of the Asia/Colombo billing month (D-38). Seeded per vehicle at registration with status FREE.';
