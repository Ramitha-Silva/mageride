-- =====================================================================================
-- 2001 — the least-privilege application roles (C127, ADD §12.6 "insider DB access", D-35)
-- Source: ADD §12.4 · ADD §12.6 · D7' §13 · migration 1305's own comment
--
-- WHY THIS EXISTS
-- ---------------
-- Two controls the platform ships were found inert on the deployed replica by the C127 ASVS
-- review, and both for the same reason: **every service connects as the role that owns every
-- table, and that role is a superuser.**
--
--   1. `audit.events` is D-35's immutable admin log. 1305 does `REVOKE UPDATE, DELETE ... FROM
--      PUBLIC` and its own comment says what that is worth: "This revokes the PUBLIC grant only:
--      it stops a role that holds no explicit privilege, not the table owner or a superuser. Real
--      immutability is the deployment's job — the service role must be granted INSERT and SELECT
--      and nothing else (D7' §13)." On the replica, `has_table_privilege('audit.events','DELETE')`
--      answered **true**, and a `DELETE` was accepted by the server.
--
--   2. Nine tables carry row-level security (1804, 1806, 1807). A **table owner bypasses RLS**
--      unless FORCE is set, and a **superuser bypasses it unconditionally**. On the replica
--      `row_security_active()` answered **false** for all nine, so every fleet-scoping policy the
--      platform ships was doing nothing at all.
--
-- Neither is a code defect — the migrations are right and the policies are right. Both are the
-- deployment step D7' §13 describes and nothing performed. This script is that step, expressed
-- once so no deployment has to remember it.
--
-- WHAT THIS SCRIPT DOES AND DOES NOT DO
-- -------------------------------------
-- It CREATES the roles and sets every grant, revoke and FORCE flag. It does NOT change who
-- connects: `ConnectionStrings__Postgres` still names whatever it named before, so applying this
-- migration cannot break a running deployment. The cutover is one environment variable and it is
-- `docs/runbooks/database-roles.md` §3.
--
-- `security/checks/40-database-privileges.sh` is what fails until the cutover happens, so the
-- half-done state is loud rather than silent.
--
-- THREE ROLES, BECAUSE THERE ARE THREE DIFFERENT AUTHORITIES
-- ----------------------------------------------------------
--   mageride_app     the services. DML on every business table; on audit.events, INSERT and
--                    SELECT and nothing else. Not an owner, so RLS applies to it.
--   mageride_migrate DDL. What DbUp connects as, and nothing else ever does.
--   mageride_readonly SELECT for analysts and the observability stack. Never granted audit writes.
--
-- Roles are cluster-scoped, so each is guarded rather than created blind — Postgres has no
-- CREATE ROLE IF NOT EXISTS. They are NOLOGIN group roles: a deployment creates its own login
-- user and grants it membership, which is what makes the D7' §13 90-day credential rotation a new
-- password on a login role rather than a re-grant of this whole matrix.
-- =====================================================================================

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mageride_app') THEN
    CREATE ROLE mageride_app NOLOGIN;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mageride_migrate') THEN
    CREATE ROLE mageride_migrate NOLOGIN;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mageride_readonly') THEN
    CREATE ROLE mageride_readonly NOLOGIN;
  END IF;
END $$;

COMMENT ON ROLE mageride_app IS
  'The role every .NET service connects as (C127). DML on the business schemas; INSERT+SELECT only on audit.events; not an owner, so row-level security applies to it. Never grant it DDL.';
COMMENT ON ROLE mageride_migrate IS
  'DbUp''s role (C127). Owns nothing at run time and is used by no service — only by MageRide.Migrations.';
COMMENT ON ROLE mageride_readonly IS
  'SELECT for analysts and the observability stack (C127). Reads audit.events and writes nothing anywhere.';

-- -------------------------------------------------------------------------------------
-- The business schemas. `audit` is deliberately absent and is handled on its own below.
--
-- A DO block rather than nineteen copies of four statements: the list is the thing to read, and
-- a schema added in a later migration is added to this list rather than to a wall of grants.
-- `ALTER DEFAULT PRIVILEGES ... FOR ROLE current_user` is what makes a table created by a LATER
-- migration inherit these, so this file does not have to be re-run after every schema change.
-- -------------------------------------------------------------------------------------
DO $$
DECLARE
  target TEXT;
  business TEXT[] := ARRAY[
    'analytics', 'billing', 'comms', 'config', 'content', 'dispatch', 'docs', 'fares',
    'iam', 'pdpa', 'prov', 'registry', 'reputation', 'rides', 'safety', 'spatial',
    'subscription', 'support', 'telemetry', 'transit', 'transit_staging', 'trips'];
BEGIN
  FOREACH target IN ARRAY business LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = target) THEN
      CONTINUE;
    END IF;

    EXECUTE format('GRANT USAGE ON SCHEMA %I TO mageride_app, mageride_readonly', target);
    EXECUTE format('GRANT USAGE, CREATE ON SCHEMA %I TO mageride_migrate', target);

    EXECUTE format(
      'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO mageride_app', target);
    EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO mageride_readonly', target);
    EXECUTE format(
      'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO mageride_app', target);
    EXECUTE format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO mageride_app', target);

    EXECUTE format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I '
      'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO mageride_app', current_user, target);
    EXECUTE format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I '
      'GRANT SELECT ON TABLES TO mageride_readonly', current_user, target);
    EXECUTE format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I '
      'GRANT USAGE, SELECT ON SEQUENCES TO mageride_app', current_user, target);
  END LOOP;
END $$;

-- -------------------------------------------------------------------------------------
-- audit — append-only, and this is the sentence 1305 could not write.
--
-- INSERT and SELECT, never UPDATE, DELETE or TRUNCATE. The REVOKE is belt and braces against an
-- ALTER DEFAULT PRIVILEGES somewhere else in the cluster having been more generous, and against
-- this file being re-run after somebody widened the grant by hand.
-- -------------------------------------------------------------------------------------
GRANT USAGE ON SCHEMA audit TO mageride_app, mageride_readonly;
GRANT USAGE, CREATE ON SCHEMA audit TO mageride_migrate;

GRANT SELECT, INSERT ON audit.events TO mageride_app;
GRANT SELECT ON audit.events TO mageride_readonly;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO mageride_app;

REVOKE UPDATE, DELETE, TRUNCATE ON audit.events FROM mageride_app, mageride_readonly, PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA audit
  GRANT SELECT, INSERT ON TABLES TO mageride_app;
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA audit
  GRANT SELECT ON TABLES TO mageride_readonly;

-- -------------------------------------------------------------------------------------
-- FORCE row-level security on every table that has it.
--
-- Not cosmetic: `mageride_app` is not the owner, so the policies would apply to it as things
-- stand — but the owner is also the role a human operator uses for a console session, and the
-- day somebody points a service at the owning role again the policies would silently stop
-- applying. FORCE makes the policy hold for the owner too, which is the only configuration where
-- "RLS is on this table" is true without qualification.
--
-- Driven off `pg_class.relrowsecurity` rather than a hard-coded list, so a table that gains RLS
-- in a later migration is forced by re-running this script — which `migrate-verify.sh` does.
-- telemetry.positions is deliberately not in the set and cannot be: 1804 records that TimescaleDB
-- refuses RLS on a compressed hypertable, and its fleet scoping is a security_barrier view.
-- -------------------------------------------------------------------------------------
DO $$
DECLARE
  relation TEXT;
BEGIN
  FOR relation IN
    SELECT format('%I.%I', n.nspname, c.relname)
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE c.relrowsecurity
       AND NOT c.relforcerowsecurity
       AND c.relkind = 'r'
  LOOP
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', relation);
  END LOOP;
END $$;

-- The fleet reader 1804 created is a *tenant* role and is unrelated to the three above; a
-- deployment grants it to whichever login user query-svc uses for Epic 13 reads. Named here only
-- so the four roles are readable in one place.
