-- =====================================================================================
-- 0708 — dispatch: durable timer backstop
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 · D5' §12.3 · DT-04, DT-08
--
-- Same durable-timer pattern as rides.timers (0605): the Redis key TTL is a fast hint, this
-- is the record a Quartz.NET job scans. Separate table because the subject is a DRIVER, not a
-- ride — a directional filter outlives any single ride and often exists with no ride at all.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.timers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  kind TEXT NOT NULL DEFAULT 'directional_expiry',
  fire_at TIMESTAMPTZ NOT NULL,
  fired_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_dispatch_timers_due
  ON dispatch.timers(fire_at) WHERE fired_at IS NULL;

COMMENT ON TABLE dispatch.timers IS
  'Durable backstop for DT-04 directional expiry and the DT-08 10-minute pre-expiry reminder. kind carries no CHECK — both specs print it open, and dispatch-svc adds kinds without a migration.';
