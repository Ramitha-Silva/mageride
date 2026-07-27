-- =====================================================================================
-- 0701 — dispatch: online driver presence
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 · D5' §3.1 · D-06, R-06
--
-- Redis (`geo:drivers:available:{type}:{h3cell}`) is the hot candidate index; this table is
-- the durable one and the source for the Job Board ST_DWithin query (D-06).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.driver_presence (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  vehicle_type TEXT NOT NULL,
  state TEXT NOT NULL DEFAULT 'OFFLINE' CONSTRAINT ck_driver_presence_state
    CHECK (state IN ('OFFLINE','AVAILABLE','OFFERED','ON_RIDE')),
  geo GEOGRAPHY(POINT,4326),                                  -- last known position
  driver_home GEOGRAPHY(POINT,4326),                          -- D-06 Job Board 30 km ST_DWithin
  last_seen_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- Partial: the candidate build only ever looks at AVAILABLE drivers, so OFFERED/ON_RIDE/OFFLINE
-- rows stay out of the index and out of its maintenance cost on every presence heartbeat.
CREATE INDEX IF NOT EXISTS ix_presence_geo
  ON dispatch.driver_presence USING gist(geo) WHERE state = 'AVAILABLE';
CREATE INDEX IF NOT EXISTS ix_presence_home
  ON dispatch.driver_presence USING gist(driver_home);

SELECT public.attach_set_updated_at('dispatch','driver_presence');

COMMENT ON TABLE dispatch.driver_presence IS
  'Durable standby-driver presence (also cached in Redis). One row per driver — going online on a second vehicle overwrites vehicle_id, which is the presence-plane echo of O2.';
