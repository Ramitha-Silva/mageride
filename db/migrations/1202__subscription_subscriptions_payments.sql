-- =====================================================================================
-- 1202 — subscription: per-subscriber fare and pass-through payments
-- Source: D4' Δ 2026-06-21 (Epic 23) · server_db_schema.md §18b · ADD §9.1
--         AL-23, AL-24, AL-25, AL-51 · D-38 · BR-23.10
--
-- NO PLATFORM COMMISSION. §18b is explicit: subscription.payments never posts to
-- billing.journal_entries. The money moves passenger → fleet owner bank-to-bank, with
-- payTo read live from the latest VERIFIED registry.fleet_payout_profiles row (AL-49).
-- Only the platform's own Mode B fee (billing.monthly_subscriptions) is ledgered.
-- That is why there is no journal_entry_id column here and must not be one.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS subscription.subscriptions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  grant_id UUID NOT NULL REFERENCES subscription.grants(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  passenger_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  -- Defaults from registry.vehicles.mode_b_billing (AL-24). Labelled "Service payment"
  -- in the UI (AL-51) — a rename only, the values are unchanged.
  billing TEXT NOT NULL CONSTRAINT ck_subscriptions_billing
    CHECK (billing IN ('paid','free')),
  -- Overridable per subscriber; NULL when free.
  monthly_fare_minor INTEGER CHECK (monthly_fare_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  cycle TEXT NOT NULL DEFAULT 'join_anniversary' CONSTRAINT ck_subscriptions_cycle
    CHECK (cycle IN ('month_first','join_anniversary')),
  -- join_anniversary: joined 5 Jun → next due 6 Jul.
  join_day SMALLINT CHECK (join_day BETWEEN 1 AND 31),
  next_due DATE,                                              -- Asia/Colombo (D-38)
  next_due_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),          -- D-38 audit companion
  status TEXT NOT NULL DEFAULT 'active' CONSTRAINT ck_subscriptions_status
    CHECK (status IN ('active','paused','cancelled')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- A paid subscription with no fare would bill nothing forever.
  CONSTRAINT ck_subscriptions_fare CHECK (billing = 'free' OR monthly_fare_minor IS NOT NULL),
  -- Not printed in either spec: a join_anniversary cycle is undefined without the day it
  -- anniversaries on, and month_first has no use for one.
  CONSTRAINT ck_subscriptions_join_day
    CHECK (cycle <> 'join_anniversary' OR join_day IS NOT NULL));

SELECT public.attach_set_updated_at('subscription','subscriptions');

-- One live subscription per grant; a cancelled one stays for history.
CREATE UNIQUE INDEX IF NOT EXISTS ux_subscriptions_grant_live
  ON subscription.subscriptions(grant_id) WHERE status <> 'cancelled';
-- The monthly due-date sweep.
CREATE INDEX IF NOT EXISTS ix_subscriptions_due
  ON subscription.subscriptions(next_due) WHERE status = 'active' AND billing = 'paid';
CREATE INDEX IF NOT EXISTS ix_subscriptions_vehicle
  ON subscription.subscriptions(vehicle_id) WHERE status = 'active';

COMMENT ON TABLE subscription.subscriptions IS
  'Per-subscriber Mode B fare and billing cycle (Epic 23). The fare is the fleet owner''s, not the platform''s — see subscription.payments.';
COMMENT ON COLUMN subscription.subscriptions.billing IS
  'Defaults from registry.vehicles.mode_b_billing (AL-24) but is overridable per subscriber. Surfaced as "Service payment" in the UI (AL-51 — label only).';

CREATE TABLE IF NOT EXISTS subscription.payments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  subscription_id UUID NOT NULL REFERENCES subscription.subscriptions(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  period_month DATE NOT NULL,                                 -- Asia/Colombo (D-38)
  period_month_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),      -- D-38 audit companion
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  method TEXT NOT NULL CONSTRAINT ck_subscription_payments_method
    CHECK (method IN ('lankaqr_deeplink','lankaqr_scan','onepay','online_transfer','cash')),
  status TEXT NOT NULL DEFAULT 'initiated' CONSTRAINT ck_subscription_payments_status
    CHECK (status IN ('initiated','pending_verification','paid','failed')),
  slip_url TEXT,                                              -- online-transfer screenshot
  gateway_ref TEXT,
  confirmed_by UUID REFERENCES iam.users(id),                 -- owner who confirmed / marked cash
  paid_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_subscription_payments_period_first_day
    CHECK (period_month = date_trunc('month', period_month)::date),
  CONSTRAINT ck_subscription_payments_paid_at
    CHECK ((status = 'paid') = (paid_at IS NOT NULL)));

-- At most one live payment per (subscription, month) — a failed attempt is exempt so the
-- passenger can retry, which is why this is partial.
CREATE UNIQUE INDEX IF NOT EXISTS ux_subpay_period
  ON subscription.payments(subscription_id, period_month)
  WHERE status IN ('initiated','pending_verification','paid');
CREATE INDEX IF NOT EXISTS ix_subpay_vehicle
  ON subscription.payments(vehicle_id, period_month);
CREATE INDEX IF NOT EXISTS ix_subpay_passenger
  ON subscription.payments(passenger_id, period_month);
-- The owner's "confirm this transfer slip" queue (SCR-FP-016).
CREATE INDEX IF NOT EXISTS ix_subpay_pending_verification
  ON subscription.payments(vehicle_id, created_at) WHERE status = 'pending_verification';

COMMENT ON TABLE subscription.payments IS
  'Subscriber-facing Mode B fare, routed to the FLEET OWNER as a pass-through (BR-23.10). Never posts to billing.journal_entries — the platform takes no commission (§18b). payTo is composed live from the verified registry.fleet_payout_profiles row, never denormalised here.';
COMMENT ON COLUMN subscription.payments.slip_url IS
  'Online-transfer screenshot. Its arrival moves the row to pending_verification until the fleet owner confirms; cash is marked received by the owner the same way.';
