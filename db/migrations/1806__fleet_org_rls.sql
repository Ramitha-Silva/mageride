-- =====================================================================================
-- 1806 — fleet-org row-level security and the fleet-scoped read path
-- Source: ADD §9.5 item 8 · US-3.24 / US-13.3 · D7' §4.2 `Fleet__RlsEnabled=true`
--         · server_db_schema.md §1, §2, §26 · AL-03, AL-49 · C058 fence
--         "Every read is row-level-security scoped to the caller's org. A cross-org read
--          is a security bug."
--
-- Numbered in the 18xx range although most of what it touches lives in `registry` and
-- `iam`: it depends on `mageride_fleet_reader` (1804), on `trips.sessions` (0501) and on
-- `registry.vehicles` (0303), so it cannot run with the tables it protects.
--
-- ---------------------------------------------------------------------------------------
-- HOW THE SCOPE IS CARRIED, AND WHY IT IS FAIL-CLOSED
--
-- fleet-svc opens a transaction, does
--
--     SET LOCAL ROLE mageride_fleet_reader;
--     SELECT set_config('app.fleet_id', $1, true);
--
-- and reads. Both settings are transaction-local, which is what makes this safe under
-- PgBouncer transaction pooling — the next transaction on the same server connection
-- cannot inherit either one. `SET LOCAL ROLE` is the load-bearing half: RLS is not applied
-- to a superuser, nor to a table's owner unless FORCE is set, and the service's login role
-- is one or both in every environment this repo runs in. Assuming the reader role for the
-- duration of the read is what puts the policies below in the path.
--
-- `current_setting('app.fleet_id', true)` returns NULL when the GUC is unset, so the
-- predicate becomes `fleet_id = NULL` -> NULL -> no rows. An unscoped read therefore sees
-- nothing rather than everything. The two-argument form is deliberate; the one-argument
-- form raises 42704, and a caller that catches the error is one retry away from an
-- unscoped read.
--
-- ---------------------------------------------------------------------------------------
-- WHY THE POLICIES ARE RESTRICTIVE AND ROLE-TARGETED
--
-- These five tables are read by services that are not fleet-svc: subscription-svc reads
-- `registry.fleet_payout_profiles` and `registry.fleet_vehicles` for the Mode B pay sheet
-- (C050), provisioning-svc reads `registry.fleets` for tracker scope (C030), iam-svc reads
-- `iam.fleet_members` to mint the `fleet_role` claim (C027), fleet-health-svc and the
-- hot path read the roster. A plain `ENABLE ROW LEVEL SECURITY` plus one permissive policy
-- would deny every one of them the moment their login role stopped being the table owner —
-- silently, as zero rows.
--
-- So each table gets a pair:
--   * a PERMISSIVE policy `TO PUBLIC USING (true)` — the platform's own services, unchanged;
--   * a RESTRICTIVE policy `TO mageride_fleet_reader` carrying the org predicate.
-- Permissive policies are OR-ed and restrictive ones are AND-ed, and a policy only applies
-- to the roles it names. The fleet reader therefore gets `true AND fleet_id = <guc>`;
-- every other role gets `true` and no restriction. The blast radius is exactly the role
-- created for this purpose.
--
-- ---------------------------------------------------------------------------------------
-- WHY THERE ARE ALSO VIEWS
--
-- RLS scopes rows in a table the reader may read. It cannot help where the fleet needs
-- columns from a table it must NOT be able to read at all — `registry.vehicles` (every
-- vehicle on the platform), `iam.users` (every person), `trips.sessions` (every Mode A/B
-- journey). Granting those and adding a policy would work and would be one forgotten
-- `WHERE` away from a platform-wide read. Instead the fleet reader is granted a
-- security-barrier view per join and holds no privilege on the base table, which is 1804's
-- convention (`<relation>_fleet`) and its reasoning, applied outside telemetry.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- The scoping predicate
-- -------------------------------------------------------------------------------------

-- STABLE, not IMMUTABLE: the GUC changes per transaction. SECURITY INVOKER (the default)
-- so it reads the caller's own setting.
CREATE OR REPLACE FUNCTION registry.current_fleet_id()
RETURNS UUID
LANGUAGE sql
STABLE
AS $$ SELECT nullif(current_setting('app.fleet_id', true), '')::uuid $$;

COMMENT ON FUNCTION registry.current_fleet_id() IS
  'The caller''s organisation, from the app.fleet_id session GUC. NULL when unset, which makes every fleet-scoped policy and view match no row (fail closed). Same GUC as telemetry.current_fleet_id() (1804) — one setting, read from the schema that needs it; change one and change both.';

COMMENT ON FUNCTION telemetry.current_fleet_id() IS
  'The caller''s fleet, from the app.fleet_id session GUC. NULL when unset, which makes every fleet-scoped view return no rows (fail closed). Twin of registry.current_fleet_id() (1806) — same GUC, same contract.';

-- The service's login role has to be able to assume the reader role. Guarded rather than
-- assumed: a deployment whose migration user lacks ADMIN OPTION should say so at migrate
-- time, not leave fleet-svc failing every read at 03:00.
DO $$
BEGIN
  EXECUTE format('GRANT mageride_fleet_reader TO %I', current_user);
EXCEPTION WHEN OTHERS THEN
  RAISE NOTICE 'could not grant mageride_fleet_reader to %: %. fleet-svc cannot SET ROLE and must run with Fleet:RlsEnabled=false until this is granted by hand.', current_user, SQLERRM;
END $$;

GRANT USAGE ON SCHEMA registry, iam, trips TO mageride_fleet_reader;
GRANT EXECUTE ON FUNCTION registry.current_fleet_id() TO mageride_fleet_reader;

-- -------------------------------------------------------------------------------------
-- The five org-owned tables
-- -------------------------------------------------------------------------------------

-- registry.fleets — keyed by `id`, not `fleet_id`; every other table below keys by fleet_id.
ALTER TABLE registry.fleets ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleets_platform ON registry.fleets;
DROP POLICY IF EXISTS fleets_org_scope ON registry.fleets;
CREATE POLICY fleets_platform ON registry.fleets
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleets_org_scope ON registry.fleets
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (id = registry.current_fleet_id());

ALTER TABLE registry.fleet_vehicles ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleet_vehicles_platform ON registry.fleet_vehicles;
DROP POLICY IF EXISTS fleet_vehicles_org_scope ON registry.fleet_vehicles;
CREATE POLICY fleet_vehicles_platform ON registry.fleet_vehicles
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleet_vehicles_org_scope ON registry.fleet_vehicles
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

ALTER TABLE registry.fleet_assignments ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleet_assignments_platform ON registry.fleet_assignments;
DROP POLICY IF EXISTS fleet_assignments_org_scope ON registry.fleet_assignments;
CREATE POLICY fleet_assignments_platform ON registry.fleet_assignments
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleet_assignments_org_scope ON registry.fleet_assignments
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

-- The one that guards money (BR-31.1): another org's bank account number is the single most
-- damaging row a cross-org read could return.
ALTER TABLE registry.fleet_payout_profiles ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS payout_profiles_platform ON registry.fleet_payout_profiles;
DROP POLICY IF EXISTS payout_profiles_org_scope ON registry.fleet_payout_profiles;
CREATE POLICY payout_profiles_platform ON registry.fleet_payout_profiles
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY payout_profiles_org_scope ON registry.fleet_payout_profiles
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

ALTER TABLE iam.fleet_members ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleet_members_platform ON iam.fleet_members;
DROP POLICY IF EXISTS fleet_members_org_scope ON iam.fleet_members;
CREATE POLICY fleet_members_platform ON iam.fleet_members
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleet_members_org_scope ON iam.fleet_members
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

GRANT SELECT ON registry.fleets, registry.fleet_vehicles, registry.fleet_assignments,
                registry.fleet_payout_profiles, iam.fleet_members
  TO mageride_fleet_reader;

COMMENT ON POLICY fleets_org_scope ON registry.fleets IS
  'AL-03: a fleet reader sees its own organisation and no other. RESTRICTIVE and role-targeted so the twenty services that read this table platform-wide are untouched (C058).';
COMMENT ON POLICY payout_profiles_org_scope ON registry.fleet_payout_profiles IS
  'AL-49/BR-31.1: another organisation''s bank details are never visible to a fleet reader, whatever the application asks for.';

-- -------------------------------------------------------------------------------------
-- The joins the fleet needs, and the base tables it must never hold
-- -------------------------------------------------------------------------------------

-- The fleet's own roster with the vehicle facts the Fleet Portal renders. `registry.vehicles`
-- is deliberately NOT granted: it holds every vehicle on the platform, and a policy over it
-- would have to be maintained in step with every future join.
CREATE OR REPLACE VIEW registry.fleet_vehicles_fleet WITH (security_barrier = true) AS
  SELECT fv.fleet_id,
         fv.vehicle_id,
         fv.mode,
         v.registration_number,
         v.vehicle_type,
         v.status,
         v.dispatch_state,
         v.mode_b_billing,
         v.default_monthly_fare_minor,
         v.driver_name,
         v.created_at,
         v.updated_at
    FROM registry.fleet_vehicles fv
    JOIN registry.vehicles v ON v.id = fv.vehicle_id
   WHERE fv.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW registry.fleet_vehicles_fleet IS
  'Fleet-scoped vehicle roster (AL-03). The only relation through which a fleet reader reaches registry.vehicles; the base table is never granted.';

-- The org's team, with the identity columns a member list shows. `iam.users` is every person
-- on the platform and is likewise never granted.
CREATE OR REPLACE VIEW iam.fleet_members_fleet WITH (security_barrier = true) AS
  SELECT m.fleet_id,
         m.user_id,
         m.fleet_role,
         m.created_at,
         u.email,
         u.first_name,
         u.is_blocked
    FROM iam.fleet_members m
    JOIN iam.users u ON u.id = m.user_id
   WHERE m.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW iam.fleet_members_fleet IS
  'Fleet-scoped team list (US-13.A5). The only relation through which a fleet reader reaches iam.users; phone is deliberately absent — a sub-user''s number is not the org''s business.';

-- ---------------------------------------------------------------------------------------
-- The journey read path.
--
-- `trips.sessions`, NOT `rides.rides`. The C058 deliverable says "a fleet-scoped read path
-- into telemetry and rides", and the relation that holds a fleet vehicle's journeys is
-- trips.sessions: R-01 and `ck_sessions_mode` make trips.* the Mode A/B tracking plane,
-- `rides.rides` the Mode C commercial aggregate, and AL-03 plus
-- `registry.fleet_vehicles.mode CHECK (mode IN ('A','B'))` mean a fleet vehicle can never
-- appear in rides.rides. A `rides.rides_fleet` view would be a relation that is empty by
-- construction, and granting it would tell a future reader that fleets have Mode C rides.
-- Raised in the C058 handoff.
-- ---------------------------------------------------------------------------------------
CREATE OR REPLACE VIEW trips.sessions_fleet WITH (security_barrier = true) AS
  SELECT s.id,
         s.vehicle_id,
         s.driver_id,
         s.mode,
         s.state,
         s.route_id,
         s.started_at,
         s.ended_at,
         s.end_reason,
         fv.fleet_id
    FROM trips.sessions s
    JOIN registry.fleet_vehicles fv ON fv.vehicle_id = s.vehicle_id
   WHERE fv.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW trips.sessions_fleet IS
  'Fleet-scoped Mode A/B journeys (US-13.4). Membership is evaluated as it stands now, so a vehicle removed from the fleet takes its history out of the portal while telemetry.positions keeps the original fleet_id for the audit trail (C006 decision 8).';

GRANT SELECT ON registry.fleet_vehicles_fleet, iam.fleet_members_fleet, trips.sessions_fleet
  TO mageride_fleet_reader;

-- Belt and braces against a permissive ALTER DEFAULT PRIVILEGES anywhere in the cluster:
-- the base tables the views exist to keep out of reach.
REVOKE ALL ON registry.vehicles FROM mageride_fleet_reader;
REVOKE ALL ON iam.users         FROM mageride_fleet_reader;
REVOKE ALL ON trips.sessions    FROM mageride_fleet_reader;
