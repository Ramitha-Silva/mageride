-- =====================================================================================
-- 0318 — registry.driver_eligible_vehicles carries the assigning fleet's name and the
--        assignment's expiry (Δ MCS-02).
--
-- WHY. SCR-DA/DI-026's "Temporarily assigned to me" group prints
-- "Lanka Fleet (Pvt) Ltd · until 30 Jun" under a fleet-assigned vehicle (US-13.9), and
-- `GET /v1/vehicles/mine` could return neither. The view already carried `fleet_id` —
-- an identifier, which is not a caption — and dropped `expires_at` after using it in the
-- WHERE clause, so the one date the row is about never reached the client.
--
-- The C069 handoff recorded this as "no expiry column exists". That was WRONG and this
-- migration is the correction: 0314 (C059) added `registry.fleet_assignments.expires_at`,
-- the exclusion constraint over its window, and the auto-expiry predicate below. What is
-- actually stale is **D4' §2 and server_db_schema.md §2**, whose printed DDL still shows
-- `(assigned_at, revoked_at)` and the superseded `ux_fleet_assign_active` index — the
-- same gap 0314's own header already asked to be closed. Both are updated in this pass.
--
-- Additive and idempotent: two columns onto a view, no table touched, no data moved.
--
-- Spec: D2' §SCR-DA-026, URD US-13.9, D4' §2, migration 0310 / 0314.
-- =====================================================================================

DROP VIEW IF EXISTS registry.driver_eligible_vehicles;

CREATE VIEW registry.driver_eligible_vehicles AS
SELECT DISTINCT ON (candidate.driver_id, candidate.vehicle_id)
       candidate.driver_id,
       candidate.vehicle_id,
       candidate.source,
       candidate.fleet_id,
       -- Δ MCS-02. The name, not just the id: a driver reads "Lanka Fleet (Pvt) Ltd", and
       -- resolving a UUID to it would need a second read of a table on the far side of a
       -- service boundary. `registry.fleets` is registry-svc's own, so this is a join.
       f.name AS fleet_name,
       -- Δ MCS-02. The window's end, which 0314 used in the WHERE clause and then dropped.
       -- NULL for an owned vehicle and for an open-ended assignment — the two are different
       -- facts and the client renders neither, so they collapse safely here.
       candidate.expires_at AS assigned_until,
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
       (v.status = 'APPROVED' AND v.dispatch_state = 'ACTIVE') AS is_go_live_eligible
  FROM (
        SELECT owner_id AS driver_id,
               id       AS vehicle_id,
               'owned'  AS source,
               NULL::uuid AS fleet_id,
               NULL::timestamptz AS expires_at
          FROM registry.vehicles

        UNION ALL

        -- Temporarily assigned (US-13.9, AL-23). Three predicates, three requirements:
        --   revoked_at IS NULL      — US-13.8, the operator took it back;
        --   valid_from <= now()     — a relief driver booked for Thursday cannot drive today;
        --   expires_at > now()      — US-13.9's auto-expiry, with nobody revoking anything.
        -- The last is the whole of C059's "an assignment expiring removes the driver's ability
        -- to select that vehicle without manual action": the row stays exactly as it was and
        -- simply stops being returned.
        SELECT a.driver_id,
               a.vehicle_id,
               'assigned' AS source,
               a.fleet_id,
               a.expires_at
          FROM registry.fleet_assignments a
         WHERE a.revoked_at IS NULL
           AND a.valid_from <= now()
           AND (a.expires_at IS NULL OR a.expires_at > now())
       ) AS candidate
  JOIN registry.vehicles v ON v.id = candidate.vehicle_id
  LEFT JOIN registry.fleets f ON f.id = candidate.fleet_id
 ORDER BY candidate.driver_id,
          candidate.vehicle_id,
          CASE candidate.source WHEN 'owned' THEN 0 ELSE 1 END;

COMMENT ON VIEW registry.driver_eligible_vehicles IS
  'Which vehicles a driver may go live on right now, and how they came by them (US-9.6, US-13.9). source = owned | assigned. An assigned row is returned only inside its validity window, so US-13.9''s auto-expiry needs no sweep and no revocation (C059). fleet_name and assigned_until are what SCR-DA/DI-026''s "Temporarily assigned to me" group prints (Δ MCS-02); both are NULL on an owned row. is_go_live_eligible is APPROVED + dispatch_state ACTIVE; the raw columns are kept so each consumer can map its own errors. registry-svc owns it; dispatch-svc, trip-state-svc and fleet-svc read it.';
