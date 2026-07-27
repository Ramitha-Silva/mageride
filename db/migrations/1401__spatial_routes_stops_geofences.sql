-- =====================================================================================
-- 1401 — spatial: PostGIS system of record
-- Source: server_db_schema.md §17 · D4' §11-16 (17a) · ADD §9.1 · §0 Spatial convention
--
-- GEOMETRY, not GEOGRAPHY, throughout: §0 reserves GEOGRAPHY(POINT,4326) for app and
-- device points where metre-accurate distance math matters, and GEOMETRY(...,4326) for
-- this system of record, where the work is containment and overlay.
--
-- Also closes the C004 deferred FK: trips.sessions.route_id → spatial.routes(id).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS spatial.routes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  route_number TEXT,
  geom GEOMETRY(LineString,4326) NOT NULL,
  -- Mode A (bus/train) routes are the ones a trips.sessions row rides along; NULL where
  -- the route is reference data only.
  mode CHAR(1) CONSTRAINT ck_routes_mode CHECK (mode IS NULL OR mode IN ('A','B','C')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_routes_geom ON spatial.routes USING gist(geom);
CREATE INDEX IF NOT EXISTS ix_routes_number
  ON spatial.routes(route_number) WHERE route_number IS NOT NULL;

COMMENT ON TABLE spatial.routes IS
  'Operator-managed route geometry (§17). Distinct from transit.gtfs_shapes, which is the imported public-transport feed — this table is MageRide''s own record.';

CREATE TABLE IF NOT EXISTS spatial.stops (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  geom GEOMETRY(Point,4326) NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_stops_geom ON spatial.stops USING gist(geom);

CREATE TABLE IF NOT EXISTS spatial.geofences (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT,
  kind TEXT,
  geom GEOMETRY(Polygon,4326) NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_geofences_geom ON spatial.geofences USING gist(geom);

COMMENT ON TABLE spatial.geofences IS
  'Operating-area and zone polygons (§17). GiST-indexed for the containment test on the booking path.';

-- -------------------------------------------------------------------------------------
-- C004 deferred FK. server_db_schema §4 writes trips.sessions.route_id REFERENCES
-- spatial.routes(id); the column landed bare in 0501 because this table is C005's.
-- ON DELETE SET NULL: retiring a route must not delete the trips that ran on it.
-- -------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'trips.sessions'::regclass
                    AND conname = 'fk_sessions_route') THEN
    ALTER TABLE trips.sessions
      ADD CONSTRAINT fk_sessions_route
      FOREIGN KEY (route_id) REFERENCES spatial.routes(id) ON DELETE SET NULL;
  END IF;
END $$;

COMMENT ON COLUMN trips.sessions.route_id IS
  'Mode A route (server_db_schema §4). FK to spatial.routes(id) added by the C005 spatial migration, which creates the target.';
