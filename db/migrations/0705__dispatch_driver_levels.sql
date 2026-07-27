-- =====================================================================================
-- 0705 — dispatch: driver level system and its no-show audit
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 · D5' §4 · US-6A.6, US-6A.7, US-6A.8
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.driver_levels (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id),
  -- Everyone starts at 3. Level 1 loses scheduled-ride / Job Board access (US-6A.8).
  level SMALLINT NOT NULL DEFAULT 3
    CONSTRAINT ck_driver_levels_level CHECK (level BETWEEN 1 AND 3),
  rating_points INTEGER NOT NULL DEFAULT 0,
  level_up_threshold INTEGER NOT NULL DEFAULT 500,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('dispatch','driver_levels');

-- Append-only audit for every level decrement (US-6A.7). ride_id carries no FK: a level
-- decrement is a permanent fact about the driver and must outlive the ride row it came from.
CREATE TABLE IF NOT EXISTS dispatch.no_show_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  ride_id UUID,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_no_show_events_driver
  ON dispatch.no_show_events(driver_id, ts DESC);

COMMENT ON TABLE dispatch.driver_levels IS
  'Driver Level System (US-6A.6, D5'' §4.2). rating_points accrue from trips.ratings; level_up_threshold is admin-configurable per PUT /v1/admin/drivers/level-config.';
COMMENT ON TABLE dispatch.no_show_events IS
  'Level-decrement audit (US-6A.7). Driver-side no-shows only; the passenger-side counter is reputation.counters.no_shows.';
