-- =====================================================================================
-- 0310 — registry: the go-live eligibility projection
-- Source: URD US-9.6, US-9.7, US-13.9 · D5' §3.2 hard eligibility gates · AL-03, AL-23,
--         AL-30, E-03, D-03
--
-- ⚠ Spec gap — micro-change-set, raised in the C028 handoff.
--   US-13.9 gives an *assigned* driver the right to "select one and go online" with a fleet
--   vehicle they did not register, in a separate "Temporarily assigned to me" group. Nothing in
--   D4' §2 or server_db_schema.md §2 answers "which vehicles may this driver operate" — the
--   fact is spread over three tables, and every consumer that needs it (registry's select-live,
--   dispatch's standby gate, trip-state's session start) would otherwise re-derive the join and
--   drift. `dispatch-svc` today reads `registry.vehicles WHERE owner_id = :driver`, which is the
--   drift already: it cannot see an assigned vehicle at all, so US-13.9 is unimplementable
--   against it.
--
--   A view rather than a table: there is no state here that is not already stored, and a
--   materialised copy would need invalidating on every assignment, approval and suspension.
--
--   **D4' §2 should carry this projection**, or D5' §3.2 should say how a consumer derives it.
--
-- ⚠ Second gap, NOT closed here. US-13.9 says the assignment "**auto-expires**", and
--   `registry.fleet_assignments` (0306) has `assigned_at` and `revoked_at` and no expiry column.
--   Nothing in D4' §2 or server_db_schema.md §2 carries one either. This view therefore honours
--   revocation (US-13.8) and cannot honour expiry; **C059 owns assignment writes and should add
--   `expires_at`**, after which the WHERE clause below gains one predicate and nothing else
--   changes. Recorded in the C028 handoff.
-- =====================================================================================

-- CREATE OR REPLACE cannot change a view's column list, so a re-run against an older shape
-- would fail. Dropping first keeps the script re-runnable, which is what migrate-verify's
-- journal-disabled pass checks. Nothing holds a dependency on it — consumers query it by name.
DROP VIEW IF EXISTS registry.driver_eligible_vehicles;

CREATE VIEW registry.driver_eligible_vehicles AS
-- One row per (driver, vehicle). A driver who both owns a vehicle and is assigned to it would
-- otherwise appear twice and a consumer's single-row read would fail; 'owned' wins, because
-- ownership outlives an assignment that can be revoked.
SELECT DISTINCT ON (candidate.driver_id, candidate.vehicle_id)
       candidate.driver_id,
       candidate.vehicle_id,
       candidate.source,
       candidate.fleet_id,
       v.owner_id,
       v.registration_number,
       v.vehicle_type,
       v.mode,
       v.status,
       v.dispatch_state,
       v.onboarding_status,
       v.driver_name,
       v.driver_photo_url,
       v.created_at,
       -- The US-9.6 gate, spelled once. APPROVED covers AL-30's auto-approval and AL-10's
       -- insurance requirement (both are upstream of the status); ACTIVE excludes the E-03
       -- document-expiry auto-suspension, which is a D5' §3.2 hard gate. Consumers that need
       -- the raw facts still have every column above — dispatch keeps its own error mapping,
       -- so an unapproved vehicle stays `vehicle-not-approved` there rather than vanishing.
       (v.status = 'APPROVED' AND v.dispatch_state = 'ACTIVE') AS is_go_live_eligible
  FROM (
        -- Owned. A driver's own Mode C registration (Driver App, AL-27) and any Mode A/B
        -- vehicle registered under their account in the Fleet Portal (AL-03).
        SELECT owner_id AS driver_id,
               id       AS vehicle_id,
               'owned'  AS source,
               NULL::uuid AS fleet_id
          FROM registry.vehicles

        UNION ALL

        -- Temporarily assigned (US-13.9, AL-23). `revoked_at IS NULL` is US-13.8: revoking an
        -- assignment removes the ability to start new sessions the moment it is written.
        SELECT a.driver_id,
               a.vehicle_id,
               'assigned' AS source,
               a.fleet_id
          FROM registry.fleet_assignments a
         WHERE a.revoked_at IS NULL
       ) AS candidate
  JOIN registry.vehicles v ON v.id = candidate.vehicle_id
 ORDER BY candidate.driver_id,
          candidate.vehicle_id,
          -- 'assigned' < 'owned' alphabetically, so order explicitly rather than by luck.
          CASE candidate.source WHEN 'owned' THEN 0 ELSE 1 END;

COMMENT ON VIEW registry.driver_eligible_vehicles IS
  'Which vehicles a driver may go live on, and how they came by them (US-9.6, US-13.9). source = owned | assigned. is_go_live_eligible is APPROVED + dispatch_state ACTIVE; the raw columns are kept so each consumer can map its own errors. registry-svc owns it; dispatch-svc and trip-state-svc read it.';

-- US-13.9's "Temporarily assigned to me" group is a per-driver read and the eligibility check is
-- a per-(driver, vehicle) read; the base tables index owner_id and driver_id already, and this is
-- the one join the view adds.
CREATE INDEX IF NOT EXISTS ix_fleet_assign_driver_active
  ON registry.fleet_assignments(driver_id, vehicle_id) WHERE revoked_at IS NULL;
