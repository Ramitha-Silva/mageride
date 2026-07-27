-- =====================================================================================
-- 1405 — analytics: dashboard rollup
-- Source: server_db_schema.md §23 (Δ 2026-06-28) · D4' Δ 2026-06-28 · AL-38 · D-38
--
-- Feeds GET /admin/dashboard/stats for period and custom-range queries, which aggregate
-- over metric_date. The live cards on the same screen — online drivers, pending
-- verifications, open tickets — are read real-time from their own services and must NOT
-- be added here; a rolled-up "currently online" is wrong by construction.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS analytics.daily_metrics (
  metric_date DATE PRIMARY KEY,                               -- Asia/Colombo (D-38)
  -- D-38 audit companion: the instant this metric day was first rolled up. Distinct from
  -- refreshed_at, which moves on every subsequent recompute.
  metric_date_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_trips INTEGER NOT NULL DEFAULT 0 CHECK (completed_trips >= 0),
  gross_fare_minor BIGINT NOT NULL DEFAULT 0 CHECK (gross_fare_minor >= 0),
  new_riders INTEGER NOT NULL DEFAULT 0 CHECK (new_riders >= 0),
  new_drivers INTEGER NOT NULL DEFAULT 0 CHECK (new_drivers >= 0),
  daily_fee_revenue_minor BIGINT NOT NULL DEFAULT 0 CHECK (daily_fee_revenue_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  refreshed_at TIMESTAMPTZ NOT NULL DEFAULT now());

COMMENT ON TABLE analytics.daily_metrics IS
  'One row per Asia/Colombo metric day (AL-38). Derived and idempotently recomputable — the source tables stay authoritative, so a rebuild is always safe.';
COMMENT ON COLUMN analytics.daily_metrics.refreshed_at IS
  'Last recompute. metric_date_tz_at records the first one, which is the D-38 audit companion for the business date itself.';
