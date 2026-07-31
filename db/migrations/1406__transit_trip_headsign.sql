-- =====================================================================================
-- 1406 — transit: the destination a bus is signed for (C056, transit-svc-routing)
-- Source: D3' transit-svc "ALL direct GTFS routes (route_no, **headsign**/desc, shape)"
--         D5' BR-23.2 "Each option carries route number, **headsign**/description, …"
--         backend/contracts/transit.yaml TransitLeg.headsign · GTFS trips.txt trip_headsign
--
-- Both specs name the headsign as part of every option and `server_db_schema` §18c gives
-- `transit.gtfs_trips` five columns, none of which can hold it: trip_id, route_id,
-- service_id, shape_id, direction. GTFS puts it in `trips.txt` as `trip_headsign`, and it
-- is the field that distinguishes the two directions of one route on a card — "138 to
-- Kottawa" and "138 to Pettah" are the same `route_short_name` and the same
-- `route_long_name`, so without it a passenger is shown the same option twice with no way
-- to tell which one goes their way.
--
-- transit-svc reads it today and falls back to `route_long_name` where it is NULL, which is
-- every row until the importer fills it. **C057 (the GTFS Dataset Manager) must map
-- trips.txt's `trip_headsign` into this column** — noted in the C056 handoff.
--
-- The staging mirror is updated in the same file, and it must be: 1404 creates
-- `transit_staging.gtfs_trips` with `CREATE TABLE IF NOT EXISTS ... LIKE transit.gtfs_trips`,
-- so LIKE only picks a new column up on a database where staging does not exist yet.
-- On every existing database the two sides would diverge — and the activation swap is
-- ALTER TABLE ... SET SCHEMA, which requires them to be shape-identical.
-- `migrate-verify.sh`'s "transit_staging mirrors the five gtfs_* tables column-for-column"
-- check is what catches that, and it passes only because of the second statement here.
-- =====================================================================================

ALTER TABLE transit.gtfs_trips
  ADD COLUMN IF NOT EXISTS trip_headsign TEXT;

ALTER TABLE transit_staging.gtfs_trips
  ADD COLUMN IF NOT EXISTS trip_headsign TEXT;

COMMENT ON COLUMN transit.gtfs_trips.trip_headsign IS
  'GTFS trips.txt trip_headsign — the destination shown on the vehicle. What tells the two directions of one route apart on an SCR-PA-009 card (D5'' BR-23.2). NULL until C057''s importer maps it; transit-svc falls back to route_long_name.';
