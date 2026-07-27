-- =====================================================================================
-- 0903 — safety: vehicle reports and passenger blocks
-- Source: server_db_schema.md §8 · D4' §8 · ADD §9.1 · D-04, US-12.6, US-12.10
--
-- Three CONFIRMED reports against one vehicle auto-delist it; reputation-svc (C033)
-- consumes this table and writes the effective state to reputation.block_states.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS safety.vehicle_reports (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  reporter_id UUID NOT NULL REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  ride_id UUID,                                               -- optional context, bare in both specs
  reason TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'PENDING' CONSTRAINT ck_vehicle_reports_status
    CHECK (status IN ('PENDING','CONFIRMED','DISMISSED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_vreports_vehicle ON safety.vehicle_reports(vehicle_id);
-- The auto-delist rule counts CONFIRMED rows per vehicle (US-12.6); the moderation queue
-- (SCR-AP-005) reads the PENDING ones.
CREATE INDEX IF NOT EXISTS ix_vreports_pending
  ON safety.vehicle_reports(created_at DESC) WHERE status = 'PENDING';

COMMENT ON TABLE safety.vehicle_reports IS
  'Passenger reports against a vehicle. Three CONFIRMED reports auto-delist it (US-12.6) — the count is evaluated by reputation-svc (D-04), not by a trigger here.';

CREATE TABLE IF NOT EXISTS safety.blocked_drivers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_blocked_drivers_pair UNIQUE (passenger_id, driver_id),
  -- Neither spec says so, but a self-block would silently shrink a driver's own candidate
  -- set if they also ride as a passenger, and nothing in the product can produce one.
  CONSTRAINT ck_blocked_drivers_not_self CHECK (passenger_id <> driver_id));

-- Dispatch excludes blocked drivers when building the candidate set (D5 §4), so the read
-- is "driver ids this passenger has blocked" — covered by the UNIQUE constraint's index.
-- The reverse direction (which passengers blocked me) has no product surface.

COMMENT ON TABLE safety.blocked_drivers IS
  'Passenger-initiated driver block (US-12.10). dispatch-svc filters these out of the candidate set; the block is one-directional and permanent until the passenger clears it.';
