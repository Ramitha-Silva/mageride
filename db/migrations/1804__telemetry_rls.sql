-- =====================================================================================
-- 1804 — telemetry: fleet scoping for Fleet Portal reads (Epic 13)
-- Source: server_db_schema.md §18 · D4' §17 · ADD §9.5 item 8
--
-- ADD §9.5 item 8: "Row-level security on fleet_id enables fleet operators (Epic 13) to
-- query only their own telemetry via query-svc without application-side filtering risk."
--
-- SPEC DIVERGENCE — row-level security cannot coexist with compression (micro-change-set,
-- see build/progress.md). The §18 block asks for both on the same hypertable:
--
--     ALTER TABLE telemetry.positions SET (timescaledb.compress, ...);   -- T-06 item 3
--     ALTER TABLE telemetry.positions ENABLE ROW LEVEL SECURITY;         -- T-06 item 8
--
-- TimescaleDB 2.28 refuses the combination in both directions:
--     ERROR: operation not supported on hypertables that have columnstore enabled
--     ERROR: columnstore cannot be used on table with row security
-- A compressed chunk stores a batch of rows as compressed arrays, so a per-row policy cannot
-- be applied without decompressing the batch first. There is no GUC that relaxes it, and RLS
-- cannot be enabled on a continuous aggregate either (it is a view).
--
-- Resolution: compression stays on the hypertable — it is the reason T-06 exists at all
-- (~10x on 30 days of raw telemetry, on a box that also hosts everything else) and nothing
-- substitutes for it. Fleet scoping is enforced instead by security-barrier views over which
-- the fleet role holds its ONLY grant, with SELECT on the base table and on the vehicle-keyed
-- rollups withheld. That preserves the property item 8 actually asks for — the filter lives
-- in the database, so query-svc (C042) cannot forget a WHERE clause — and it is strictly
-- fail-closed where the printed policy is not: `current_setting('app.fleet_id')` raises
-- 42704 when the GUC is unset, while `current_setting('app.fleet_id', true)` returns NULL and
-- the predicate then matches no row.
--
-- Convention: the fleet-scoped counterpart of a telemetry relation is its name + `_fleet`.
-- A fleet reader is granted those and nothing else. positions_1m / _5m / _1h are keyed by
-- vehicle only and carry no fleet_id, so they stay platform-only — a fleet-facing rollup
-- would have to be re-grouped, which is C042's call, not a schema change here.
-- =====================================================================================

-- A NOLOGIN group role. Per-tenant or per-service login users are granted membership; nothing
-- connects as this role directly. Roles are cluster-scoped, so this is guarded rather than
-- CREATE ROLE IF NOT EXISTS (which Postgres does not have).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mageride_fleet_reader') THEN
    CREATE ROLE mageride_fleet_reader NOLOGIN;
  END IF;
END $$;

COMMENT ON ROLE mageride_fleet_reader IS
  'Fleet Portal / query-svc read role (Epic 13). Holds SELECT on the telemetry.*_fleet views only; every row it can see is filtered by the app.fleet_id GUC. Never grant it telemetry.positions.';

-- The scoping predicate, in one place. STABLE, not IMMUTABLE: the GUC changes per session.
-- The second argument makes a missing setting return NULL instead of raising, which is what
-- makes an unscoped connection see nothing rather than crash.
CREATE OR REPLACE FUNCTION telemetry.current_fleet_id()
RETURNS UUID
LANGUAGE sql
STABLE
AS $$ SELECT nullif(current_setting('app.fleet_id', true), '')::uuid $$;

COMMENT ON FUNCTION telemetry.current_fleet_id() IS
  'The caller''s fleet, from the app.fleet_id session GUC. NULL when unset, which makes every fleet-scoped view return no rows (fail closed).';

-- security_barrier: the fleet predicate is evaluated before any user-supplied qual, so a
-- cheap leaky function in a WHERE clause cannot be used to read another fleet''s rows out of
-- an error message. The views are NOT security_invoker, so they run with the owner's rights
-- and the fleet role needs no privilege on telemetry.positions at all.
CREATE OR REPLACE VIEW telemetry.positions_fleet WITH (security_barrier = true) AS
  SELECT vehicle_id, sample_ts, received_ts, seq, lat, lng, speed_mps, heading_deg,
         accuracy_m, hdop, sat_count, source, fleet_id, trip_id
    FROM telemetry.positions
   WHERE fleet_id IS NOT NULL
     AND fleet_id = telemetry.current_fleet_id();

CREATE OR REPLACE VIEW telemetry.fleet_health_5m_fleet WITH (security_barrier = true) AS
  SELECT fleet_id, bucket, active_vehicles, samples
    FROM telemetry.fleet_health_5m
   WHERE fleet_id = telemetry.current_fleet_id();

COMMENT ON VIEW telemetry.positions_fleet IS
  'Fleet-scoped raw telemetry (ADD §9.5 item 8). Rows are filtered by the app.fleet_id GUC; vehicles that belong to no fleet are invisible to every fleet reader. This is the only telemetry relation a fleet reader may hold SELECT on.';
COMMENT ON VIEW telemetry.fleet_health_5m_fleet IS
  'Fleet-scoped view of telemetry.fleet_health_5m, filtered by the app.fleet_id GUC.';

-- Explicit, even though Postgres grants none of this by default: a permissive
-- ALTER DEFAULT PRIVILEGES anywhere in the cluster must not be able to widen the raw table.
REVOKE ALL ON telemetry.positions      FROM PUBLIC;
REVOKE ALL ON telemetry.positions_1m   FROM PUBLIC;
REVOKE ALL ON telemetry.positions_5m   FROM PUBLIC;
REVOKE ALL ON telemetry.positions_1h   FROM PUBLIC;
REVOKE ALL ON telemetry.fleet_health_5m FROM PUBLIC;

GRANT USAGE ON SCHEMA telemetry TO mageride_fleet_reader;
GRANT SELECT ON telemetry.positions_fleet         TO mageride_fleet_reader;
GRANT SELECT ON telemetry.fleet_health_5m_fleet   TO mageride_fleet_reader;
GRANT EXECUTE ON FUNCTION telemetry.current_fleet_id() TO mageride_fleet_reader;
