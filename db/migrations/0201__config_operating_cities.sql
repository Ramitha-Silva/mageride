-- =====================================================================================
-- 0201 — config: launch / operating cities
-- Source: server_db_schema.md §17b · D4' §17b · AL-27
--
-- Source of truth for the launch-city radio list on the first-run language/city screen
-- (SCR-DA-002 / SCR-DI-002), served read-only and cacheable via GET /v1/config/cities.
-- Admins launch a new city by inserting a row — no app release.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS config.operating_cities (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  code TEXT NOT NULL UNIQUE,                                  -- short slug, e.g. 'colombo'
  name_en TEXT NOT NULL,
  name_si TEXT NOT NULL,
  name_ta TEXT NOT NULL,                                      -- trilingual labels (D-26)
  centroid_lat DOUBLE PRECISION NOT NULL CHECK (centroid_lat BETWEEN -90 AND 90),
  centroid_lng DOUBLE PRECISION NOT NULL CHECK (centroid_lng BETWEEN -180 AND 180),
  is_active BOOLEAN NOT NULL DEFAULT true,                    -- only active cities are shown / bookable
  sort_order INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_operating_cities_active
  ON config.operating_cities(sort_order) WHERE is_active;

SELECT public.attach_set_updated_at('config','operating_cities');

COMMENT ON TABLE config.operating_cities IS
  'Admin-managed launch cities (AL-27). The chosen code persists on iam.users.operating_city_code and seeds the map centroid.';

-- The city chosen at onboarding (AL-27, US-1.3a). Added here rather than in 0101 because
-- the FK target has to exist first.
ALTER TABLE iam.users
  ADD COLUMN IF NOT EXISTS operating_city_code TEXT REFERENCES config.operating_cities(code);

CREATE INDEX IF NOT EXISTS ix_users_operating_city ON iam.users(operating_city_code);

-- Launch cities, seeded from server_db_schema.md §20. Colombo first: it is also the map
-- centroid default.
INSERT INTO config.operating_cities(code, name_en, name_si, name_ta, centroid_lat, centroid_lng, sort_order) VALUES
  ('colombo','Colombo','කොළඹ','கொழும்பு',6.9271,79.8612,0),
  ('kandy','Kandy','මහනුවර','கண்டி',7.2906,80.6337,1),
  ('galle','Galle','ගාල්ල','காலி',6.0535,80.2210,2)
ON CONFLICT (code) DO NOTHING;
