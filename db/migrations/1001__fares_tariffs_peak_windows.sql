-- =====================================================================================
-- 1001 — fares: Mode C tariffs and peak/night windows
-- Source: server_db_schema.md §9 · D4' §9 · ADD §9.1 · D-10, AL-09
--
-- Mode C only. Mode B has no per-trip fare (monthly charge, billing.monthly_subscriptions
-- for the platform fee and subscription.* for the subscriber-facing fare); Mode A is free.
-- Seed rows are in 1901.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS fares.tariffs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- Not FK-constrained to a vehicle-type lookup: the canonical set is a CHECK on
  -- registry.vehicles (AL-09) and there is no catalog table to point at.
  vehicle_type TEXT NOT NULL,
  first_km_minor INTEGER NOT NULL CHECK (first_km_minor >= 0),
  per_km_minor INTEGER NOT NULL CHECK (per_km_minor >= 0),
  peak_surcharge_pct SMALLINT NOT NULL DEFAULT 20 CHECK (peak_surcharge_pct >= 0),
  night_surcharge_pct SMALLINT NOT NULL DEFAULT 15 CHECK (night_surcharge_pct >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  -- Tariffs are versioned by effective_from rather than mutated: a completed ride must
  -- remain reconcilable against the rate that priced it (D-10).
  effective_from TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_tariffs_type_effective UNIQUE (vehicle_type, effective_from));

-- fare-svc resolves "the tariff in force for this type at this instant" on every estimate
-- and every settlement.
CREATE INDEX IF NOT EXISTS ix_tariffs_lookup
  ON fares.tariffs(vehicle_type, effective_from DESC);

COMMENT ON TABLE fares.tariffs IS
  'Mode C fare table: first-km charge + per-km rate + peak/night surcharge percentages, per vehicle type (AL-09). Versioned by effective_from — never updated in place.';

CREATE TABLE IF NOT EXISTS fares.peak_windows (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kind TEXT NOT NULL CONSTRAINT ck_peak_windows_kind
    CHECK (kind IN ('peak','night')),
  -- Local wall-clock in Asia/Colombo (D-38). TIME, not TIMESTAMPTZ: these are recurring
  -- daily windows, not instants.
  start_local TIME NOT NULL,
  end_local TIME NOT NULL,
  multiplier_pct SMALLINT NOT NULL CHECK (multiplier_pct >= 0));

COMMENT ON TABLE fares.peak_windows IS
  'Admin-configurable peak and night windows, evaluated in Asia/Colombo (D-38, D5 §2). A window may wrap midnight (night 22:00-05:00), so end_local < start_local is legal and fare-svc must handle it.';
COMMENT ON COLUMN fares.peak_windows.end_local IS
  'May be earlier than start_local — the night window wraps midnight. No CHECK enforces ordering for exactly that reason.';
