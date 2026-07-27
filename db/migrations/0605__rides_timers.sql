-- =====================================================================================
-- 0605 — rides: durable timer backstop
-- Source: server_db_schema.md §5 · D4' §5 · ADD §9.1 · D5' §3.5/§6.3 · R-04
--
-- Redis key TTLs are the fast path; this table is the durable one. A Quartz.NET clustered job
-- scans `WHERE fired_at IS NULL AND fire_at <= now()`, so a Redis flush or a restart cannot
-- lose an offer expiry, an arrival grace or a COD reconciliation window.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.timers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CONSTRAINT ck_timers_kind CHECK (kind IN
    ('offer_expiry','arrival_grace','no_show','payment_pending',
     'offline_grace','location_request_expiry','otp_attempt_window','cod_uncollected')),
  fire_at TIMESTAMPTZ NOT NULL,
  fired_at TIMESTAMPTZ,                                       -- NULL until the job runs it
  payload JSONB);

CREATE INDEX IF NOT EXISTS ix_timers_due
  ON rides.timers(fire_at) WHERE fired_at IS NULL;

COMMENT ON TABLE rides.timers IS
  'R-04 durable backstop for the ride aggregate. Per-state grace windows come from D5'' §6.3 (offline-after-accept 60 s, after-arrive 120 s, in-progress 5 min, at-payment 10 min).';
