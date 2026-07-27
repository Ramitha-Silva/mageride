-- =====================================================================================
-- 0501 — trips: Mode A/B tracking sessions
-- Source: server_db_schema.md §4 · D4' §4 · ADD §9.1 · D-03, R-01, US-5.4, US-9.6
--
-- R-01 fence: this schema is Mode A (public transport) and Mode B (private/shared)
-- TRACKING ONLY, owned by trip-state-svc. The Mode C commercial ride aggregate lives in
-- rides.rides (0601) and is owned by ride-svc. The mode CHECK below is that fence in DDL.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS trips.sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  -- R-01: 'C' is deliberately absent. A Mode C journey is a rides.rides row.
  mode CHAR(1) NOT NULL CONSTRAINT ck_sessions_mode CHECK (mode IN ('A','B')),
  state TEXT NOT NULL DEFAULT 'ACTIVE'
    CONSTRAINT ck_sessions_state CHECK (state IN ('ACTIVE','COMPLETED')),
  -- server_db_schema §4 writes REFERENCES spatial.routes(id); spatial.* is C005's, so the
  -- column lands untyped-by-FK here (D4' §4 prints it the same way). C005 must add the
  -- constraint once spatial.routes exists — see the C004 handoff note.
  route_id UUID,                                              -- Mode A route
  auto_end_at_destination BOOLEAN NOT NULL DEFAULT false,     -- US-5.4
  destination_geo GEOGRAPHY(POINT,4326),                      -- 100 m geofence end
  end_reason TEXT CONSTRAINT ck_sessions_end_reason
    CHECK (end_reason IN ('driver_ended','idle_timeout','geofence','admin')),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- D-03 / US-9.6: a driver may have only ONE vehicle live at a time. This is the tracking-plane
-- half of O2; the ride-plane half is ux_rides_driver_busy (0601).
CREATE UNIQUE INDEX IF NOT EXISTS ux_sessions_active_driver
  ON trips.sessions(driver_id) WHERE state = 'ACTIVE';
CREATE INDEX IF NOT EXISTS ix_sessions_vehicle
  ON trips.sessions(vehicle_id, started_at DESC);

SELECT public.attach_set_updated_at('trips','sessions');

COMMENT ON TABLE trips.sessions IS
  'Mode A/B journey session (D-03). Owned by trip-state-svc. "Is the device live-streaming GPS" — not a commercial booking; Mode C bookings are rides.rides (R-01).';
COMMENT ON COLUMN trips.sessions.route_id IS
  'Mode A route. FK to spatial.routes(id) is deferred to C005, which creates that table.';
COMMENT ON INDEX trips.ux_sessions_active_driver IS
  'D-03 / US-9.6: a driver can go live on only one vehicle at a time.';
