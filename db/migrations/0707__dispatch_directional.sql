-- =====================================================================================
-- 0707 — dispatch: Directional Travel (Destination Filter)
-- Source: server_db_schema.md §6, §20 · D4' §6 · ADD §9.1 · D5' §12
--         · DT-01, DT-02, DT-03, DT-04, DT-05, DT-06, DT-07, D-38
--
-- A driver heading home sets a destination; dispatch keeps them in a round only for rides
-- that make progress along that vector. The predicate REMOVES otherwise-eligible candidates
-- and never relaxes a hard gate (DT-05), so the ride aggregate is untouched by it (ADD
-- Appendix B.2 invariant 7).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.directional_filters (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  destination_geo GEOGRAPHY(POINT,4326) NOT NULL,
  label TEXT,                                                 -- e.g. "Home"
  set_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,                            -- set_at + max_duration_sec
  cleared_at TIMESTAMPTZ,
  cleared_reason TEXT CONSTRAINT ck_directional_cleared_reason
    CHECK (cleared_reason IN ('expiry','manual','offline','first_matched_trip')),
  -- DT-03 daily-use limit. One row per ACTIVATION, so the limit is
  -- COUNT(*) per (driver_id, used_date) <= max_uses_per_day — a manual turn-off still
  -- consumes the use it was created with (US-6A.19, anti-gaming). ADD §1.15's DT-03 row
  -- mentions a `use_count` column; ADD §9.1 and both DDL sources use the COUNT(*) form.
  used_date DATE NOT NULL
    DEFAULT ((now() AT TIME ZONE 'Asia/Colombo')::date),
  -- D-38: a business DATE column carries the instant it was derived from, so the
  -- Asia/Colombo boundary a row landed on is auditable after the fact.
  used_date_tz_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_directional_cleared_pair CHECK (
    (cleared_at IS NULL) = (cleared_reason IS NULL)));

-- DT-03 / ADD §9.1: at most one active filter per driver.
CREATE UNIQUE INDEX IF NOT EXISTS ux_directional_active
  ON dispatch.directional_filters(driver_id) WHERE cleared_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_directional_uses
  ON dispatch.directional_filters(driver_id, used_date);

COMMENT ON TABLE dispatch.directional_filters IS
  'Directional Travel filter (DT-01, DT-03). One row per activation; the Redis key driver:directional:{driverId} is a fast hint over this row, never the record.';
COMMENT ON COLUMN dispatch.directional_filters.used_date IS
  'Asia/Colombo business date of the activation (D-38). Daily-use enforcement is COUNT(*) per (driver_id, used_date) <= dispatch.directional_config.max_uses_per_day.';
COMMENT ON COLUMN dispatch.directional_filters.used_date_tz_at IS
  'D-38 audit companion: the exact instant used_date was derived from.';
COMMENT ON INDEX dispatch.ux_directional_active IS
  'DT-03: at most one uncleared filter per driver.';

-- Admin-tunable predicate parameters (DT-02), single row id=1. A table rather than app config
-- because PUT /v1/admin/dispatch/directional-config changes them at runtime and every
-- dispatch-svc replica must agree instantly.
CREATE TABLE IF NOT EXISTS dispatch.directional_config (
  id SMALLINT PRIMARY KEY DEFAULT 1
    CONSTRAINT ck_directional_config_singleton CHECK (id = 1),
  theta_max_deg SMALLINT NOT NULL DEFAULT 45,                 -- angular tolerance
  detour_max_m INTEGER NOT NULL DEFAULT 2000,                 -- pickup detour ceiling
  progress_min_m INTEGER NOT NULL DEFAULT 250,               -- minimum progress toward destination
  max_uses_per_day SMALLINT NOT NULL DEFAULT 2,
  max_duration_sec INTEGER NOT NULL DEFAULT 7200,             -- 2 h
  clear_on_first_trip BOOLEAN NOT NULL DEFAULT false);

-- §20 seed.
INSERT INTO dispatch.directional_config(id) VALUES (1) ON CONFLICT (id) DO NOTHING;

COMMENT ON TABLE dispatch.directional_config IS
  'Admin-configurable Directional Travel parameters (DT-02), exactly one row. Defaults are the D5'' §12.1 values: 45°, 2 km detour, 250 m progress, 2 uses/day, 2 h.';
