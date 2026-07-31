-- =====================================================================================
-- 1408 — spatial: the fleet a geofence belongs to, and the schedule's route
-- Source: backend/contracts/fleet.yaml setFleetGeofences / createFleetSchedule
--         · US-13.5, US-13.11 · server_db_schema.md §17 · AL-03
--
-- C059 (fleet-svc-fleet-ops). The two halves of 0314 that could not run from the registry
-- range, because `spatial.routes` and `spatial.geofences` are created in this one. The
-- same split 0501 and 1401 already made for `trips.sessions.route_id`.
--
-- ⚠ A GEOFENCE BELONGS TO NOBODY — micro-change-set, raised in the C059 handoff.
--   `PUT /v1/fleets/{fleetId}/geofences` stores an organisation's operational polygons and
--   `spatial.geofences` (1401, §17) has `id`, `name`, `kind`, `geom`, `created_at` and no
--   owner at all. A PUT is a replace, so without an owner one operator's upload would
--   delete every other operator's fences — and the Phase 3 alerting path this table exists
--   to feed would raise one org's zone against another org's bus.
--   Nullable, because §17's own use is the platform's operating-area polygons, which belong
--   to no fleet and must keep working unchanged.
--   **server_db_schema.md §17 / D4' §11-16 should carry the column.**
-- =====================================================================================

ALTER TABLE spatial.geofences
  ADD COLUMN IF NOT EXISTS fleet_id UUID REFERENCES registry.fleets(id) ON DELETE CASCADE;

-- Partial: the platform's own polygons carry no fleet and would otherwise be most of the
-- index. The read this serves is "this org's fences", which is every geofence request the
-- Fleet Portal makes.
CREATE INDEX IF NOT EXISTS ix_geofences_fleet
  ON spatial.geofences(fleet_id) WHERE fleet_id IS NOT NULL;

COMMENT ON COLUMN spatial.geofences.fleet_id IS
  'The organisation whose operational zone this is (US-13.5, fleet.yaml setFleetGeofences). NULL for §17''s platform operating-area polygons, which belong to no fleet. Without it one operator''s PUT would replace every operator''s fences (C059).';
COMMENT ON TABLE spatial.geofences IS
  'Operating-area and zone polygons (§17). GiST-indexed for the containment test on the booking path. fleet_id scopes an operator''s own zones; the route-deviation and geofence alerting that consumes them is Phase 3 and is deliberately not built (US-13.5).';

-- ---------------------------------------------------------------------------------------
-- The foreign key 0314 deferred, for 1401's reason, on 1401's terms.
-- ON DELETE SET NULL: retiring a route must not delete the departures that ran on it.
-- ---------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.fleet_schedules'::regclass
                    AND conname = 'fk_fleet_schedules_route') THEN
    ALTER TABLE registry.fleet_schedules
      ADD CONSTRAINT fk_fleet_schedules_route
      FOREIGN KEY (route_id) REFERENCES spatial.routes(id) ON DELETE SET NULL;
  END IF;
END $$;

COMMENT ON COLUMN registry.fleet_schedules.route_id IS
  'The Mode A route this departure runs (US-13.11), the same column trips.sessions.route_id names. FK added here because spatial.routes is created in this range, exactly as 1401 added trips.sessions''.';
