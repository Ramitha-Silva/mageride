-- =====================================================================================
-- 1402 — transit: GTFS core tables
-- Source: server_db_schema.md §18c · D4' Δ 2026-06-21 (transit) · ADD §9.1
--         AL-18 · US-8.2a / US-8.2b
--
-- Backs transit-svc's direct public-bus route matching. Keys are the feed's own GTFS ids
-- (TEXT), not surrogate UUIDs — a full-feed reimport must be able to reproduce exactly the
-- same rows, and the §0 UUID convention would defeat that.
--
-- These five tables are the ONLY ones transit_staging mirrors (1404).
-- transit.gtfs_feed_versions (1403) is the lifecycle ledger and is never staged or swapped.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS transit.gtfs_routes (
  route_id TEXT PRIMARY KEY,
  agency TEXT,
  route_short_name TEXT,
  route_long_name TEXT,
  route_type INTEGER);

COMMENT ON TABLE transit.gtfs_routes IS
  'GTFS routes.txt (AL-18). route_id is the feed''s own identifier so a reimport is reproducible.';

CREATE TABLE IF NOT EXISTS transit.gtfs_trips (
  trip_id TEXT PRIMARY KEY,
  route_id TEXT NOT NULL REFERENCES transit.gtfs_routes(route_id) ON DELETE CASCADE,
  service_id TEXT,
  shape_id TEXT,
  direction SMALLINT);

CREATE INDEX IF NOT EXISTS ix_gtfs_trips_route ON transit.gtfs_trips(route_id);

CREATE TABLE IF NOT EXISTS transit.gtfs_stops (
  stop_id TEXT PRIMARY KEY,
  name TEXT,
  -- GEOGRAPHY here, unlike spatial.* (§0): the query is "stops within 400 m of this
  -- point", which is metre distance math on the sphere.
  geo GEOGRAPHY(POINT,4326));

CREATE INDEX IF NOT EXISTS ix_gtfs_stops_geo ON transit.gtfs_stops USING gist(geo);

COMMENT ON INDEX transit.ix_gtfs_stops_geo IS
  'Backs the 400 m stop-radius lookup on the US-8.2b routing path.';

CREATE TABLE IF NOT EXISTS transit.gtfs_stop_times (
  trip_id TEXT NOT NULL REFERENCES transit.gtfs_trips(trip_id) ON DELETE CASCADE,
  stop_id TEXT NOT NULL REFERENCES transit.gtfs_stops(stop_id) ON DELETE CASCADE,
  stop_sequence INTEGER NOT NULL,
  -- INTERVAL, not TIME: GTFS expresses after-midnight service as 25:10:00 relative to the
  -- service day, which TIME cannot hold.
  arr INTERVAL,
  dep INTERVAL,
  PRIMARY KEY (trip_id, stop_sequence));

CREATE INDEX IF NOT EXISTS ix_gtfs_stop_times_stop ON transit.gtfs_stop_times(stop_id);

COMMENT ON COLUMN transit.gtfs_stop_times.arr IS
  'Offset from the start of the service day, so values may exceed 24 h (GTFS after-midnight trips). Service-calendar evaluation is Asia/Colombo (D-38).';

CREATE TABLE IF NOT EXISTS transit.gtfs_shapes (
  shape_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  geo GEOGRAPHY(POINT,4326),
  PRIMARY KEY (shape_id, seq));

COMMENT ON TABLE transit.gtfs_shapes IS
  'GTFS shapes.txt as ordered points. gtfs_trips.shape_id points here but is deliberately unconstrained — a feed may reference a shape it does not ship, and the import must not fail on it.';
