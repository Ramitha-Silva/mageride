-- =====================================================================================
-- 1404 — transit_staging: GTFS import target
-- Source: server_db_schema.md §18c / §27 · D4' Δ 2026-07-22 #2 · AL-54 · BR-32.2/32.3
--
-- The importer loads a candidate feed here, validates it, and activation swaps staging
-- into transit.* in ONE transaction (ALTER TABLE ... SET SCHEMA both ways), then issues
-- NOTIFY transit_feed_activated so transit-svc reloads its cache. The live tables
-- therefore never hold a partial feed.
--
-- The swap only works if the two sides are shape-identical, so each mirror is declared
-- with LIKE ... INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS rather than a
-- copy-pasted column list: a column added to transit.gtfs_* by a later migration cannot
-- silently diverge here. Keys, indexes and foreign keys are not copied by LIKE, so they
-- are declared explicitly below — pointing WITHIN transit_staging, never at the live
-- tables, or the swap would drag the live rows along with it.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS transit_staging.gtfs_routes (
  LIKE transit.gtfs_routes INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);
CREATE TABLE IF NOT EXISTS transit_staging.gtfs_trips (
  LIKE transit.gtfs_trips INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);
CREATE TABLE IF NOT EXISTS transit_staging.gtfs_stops (
  LIKE transit.gtfs_stops INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);
CREATE TABLE IF NOT EXISTS transit_staging.gtfs_stop_times (
  LIKE transit.gtfs_stop_times INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);
CREATE TABLE IF NOT EXISTS transit_staging.gtfs_shapes (
  LIKE transit.gtfs_shapes INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);

-- LIKE copies neither NOT NULL-by-primary-key nor the keys themselves. Re-declared here
-- with the same shape as the live side.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_routes'::regclass AND contype = 'p') THEN
    ALTER TABLE transit_staging.gtfs_routes ADD PRIMARY KEY (route_id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_trips'::regclass AND contype = 'p') THEN
    ALTER TABLE transit_staging.gtfs_trips ADD PRIMARY KEY (trip_id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_stops'::regclass AND contype = 'p') THEN
    ALTER TABLE transit_staging.gtfs_stops ADD PRIMARY KEY (stop_id);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_stop_times'::regclass AND contype = 'p') THEN
    ALTER TABLE transit_staging.gtfs_stop_times ADD PRIMARY KEY (trip_id, stop_sequence);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_shapes'::regclass AND contype = 'p') THEN
    ALTER TABLE transit_staging.gtfs_shapes ADD PRIMARY KEY (shape_id, seq);
  END IF;

  -- gtfs_trips.route_id is NOT NULL on the live side; LIKE carries that, but the FK does
  -- not come with it.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_trips'::regclass
                    AND conname = 'gtfs_trips_route_id_fkey') THEN
    ALTER TABLE transit_staging.gtfs_trips
      ADD CONSTRAINT gtfs_trips_route_id_fkey
      FOREIGN KEY (route_id) REFERENCES transit_staging.gtfs_routes(route_id) ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_stop_times'::regclass
                    AND conname = 'gtfs_stop_times_trip_id_fkey') THEN
    ALTER TABLE transit_staging.gtfs_stop_times
      ADD CONSTRAINT gtfs_stop_times_trip_id_fkey
      FOREIGN KEY (trip_id) REFERENCES transit_staging.gtfs_trips(trip_id) ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'transit_staging.gtfs_stop_times'::regclass
                    AND conname = 'gtfs_stop_times_stop_id_fkey') THEN
    ALTER TABLE transit_staging.gtfs_stop_times
      ADD CONSTRAINT gtfs_stop_times_stop_id_fkey
      FOREIGN KEY (stop_id) REFERENCES transit_staging.gtfs_stops(stop_id) ON DELETE CASCADE;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_staging_gtfs_trips_route ON transit_staging.gtfs_trips(route_id);
CREATE INDEX IF NOT EXISTS ix_staging_gtfs_stops_geo ON transit_staging.gtfs_stops USING gist(geo);
CREATE INDEX IF NOT EXISTS ix_staging_gtfs_stop_times_stop ON transit_staging.gtfs_stop_times(stop_id);

COMMENT ON SCHEMA transit_staging IS
  'GTFS importer target (AL-54). Shape-identical to the five transit.gtfs_* tables; activation swaps the two schemas in one transaction so the live feed is never partial.';
