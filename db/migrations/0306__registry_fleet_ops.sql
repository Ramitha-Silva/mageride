-- =====================================================================================
-- 0306 — registry: fleet vehicle roster, driver assignment, Mode B sharing, operators
-- Source: server_db_schema.md §2 · D4' §2 · AL-03, D-22
-- =====================================================================================

-- A fleet operates Mode A and/or Mode B only — never Mode C (AL-03).
CREATE TABLE IF NOT EXISTS registry.fleet_vehicles (
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  mode CHAR(1) NOT NULL CHECK (mode IN ('A','B')),
  PRIMARY KEY (fleet_id, vehicle_id));

CREATE INDEX IF NOT EXISTS ix_fleet_vehicles_vehicle ON registry.fleet_vehicles(vehicle_id);

-- Driver ↔ vehicle assignment (US-13.2/13.9).
CREATE TABLE IF NOT EXISTS registry.fleet_assignments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  revoked_at TIMESTAMPTZ);

CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_assign_active
  ON registry.fleet_assignments(vehicle_id, driver_id) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_fleet_assign_fleet ON registry.fleet_assignments(fleet_id);
CREATE INDEX IF NOT EXISTS ix_fleet_assign_driver ON registry.fleet_assignments(driver_id);

-- Mode B sharing grant (D-22): who may see a private vehicle's live position.
CREATE TABLE IF NOT EXISTS registry.shares (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  grantee_user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  state TEXT NOT NULL DEFAULT 'PENDING'
    CHECK (state IN ('PENDING','ACCEPTED','REVOKED','EXPIRED')),
  expires_at TIMESTAMPTZ,
  accepted_at TIMESTAMPTZ,
  revoked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE UNIQUE INDEX IF NOT EXISTS ux_shares_active
  ON registry.shares(vehicle_id, grantee_user_id) WHERE state IN ('PENDING','ACCEPTED');
CREATE INDEX IF NOT EXISTS ix_shares_grantee ON registry.shares(grantee_user_id);

SELECT public.attach_set_updated_at('registry','shares');

-- Legacy fleet-org stub. Kept because prov.tracker_bindings.fleet_id references it rather
-- than registry.fleets (server_db_schema.md §3); see the C003 handoff note.
CREATE TABLE IF NOT EXISTS registry.operators (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

COMMENT ON TABLE registry.operators IS
  'Legacy fleet-org stub referenced by prov.tracker_bindings.fleet_id. registry.fleets (AL-03) is the current fleet organisation.';
