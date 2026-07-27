-- =====================================================================================
-- 0303 — registry: vehicles
-- Source: server_db_schema.md §2 · D4' §2, Δ 2026-06-21 · AL-09, AL-24, AL-30, AL-51, D-37, E-03
--
-- The column set is the union of the two specs: server_db_schema.md §2 carries
-- mode_b_billing / default_monthly_fare_minor (AL-24) and D4' §2 carries onboarding_status
-- (AL-30). Neither has both.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.vehicles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),
  registration_number TEXT NOT NULL,
  -- AL-09 canonical types. There is no 'car' — it maps to 'sedan'.
  vehicle_type TEXT NOT NULL CHECK (vehicle_type IN
    ('motorbike','three_wheeler','flex','sedan','mini_van','van','truck','mini_truck','bus','train')),
  mode CHAR(1) NOT NULL CHECK (mode IN ('A','B','C')),
  status TEXT NOT NULL DEFAULT 'PENDING'
    CHECK (status IN ('PENDING','APPROVED','REJECTED','DEACTIVATED')),
  rejection_reason TEXT,                                      -- US-2.15
  driver_name TEXT NOT NULL,                                  -- shown to passengers (US-2.12)
  driver_photo_url TEXT,
  vehicle_photo_url TEXT,
  dispatch_state TEXT NOT NULL DEFAULT 'ACTIVE'
    CHECK (dispatch_state IN ('ACTIVE','DISPATCH_SUSPENDED')),   -- E-03 doc-expiry auto-suspend
  -- AL-30: derived from registry.onboarding_steps; only 'approved' Mode-C vehicles go live.
  onboarding_status TEXT NOT NULL DEFAULT 'incomplete'
    CHECK (onboarding_status IN ('incomplete','approved')),
  -- AL-24 / AL-51: the UI label is "Service payment (Free/Paid)"; the column name is unchanged.
  mode_b_billing TEXT CHECK (mode_b_billing IN ('paid','free')),   -- NULL for Mode A/C
  default_monthly_fare_minor INTEGER CHECK (default_monthly_fare_minor >= 0),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- D-37: registration numbers are unique across the LIVE set only, so a rejected or
-- deactivated registration does not permanently burn a plate.
CREATE UNIQUE INDEX IF NOT EXISTS ux_vehicles_regno_active
  ON registry.vehicles(registration_number) WHERE status IN ('PENDING','APPROVED');
CREATE INDEX IF NOT EXISTS ix_vehicles_owner ON registry.vehicles(owner_id);
CREATE INDEX IF NOT EXISTS ix_vehicles_mode_status ON registry.vehicles(mode, status);

SELECT public.attach_set_updated_at('registry','vehicles');

COMMENT ON COLUMN registry.vehicles.mode_b_billing IS
  'AL-24/AL-51 "Service payment": paid|free for Mode B, NULL for Mode A/C. paid requires a verified registry.fleet_payout_profiles row (BR-31.1).';
COMMENT ON INDEX registry.ux_vehicles_regno_active IS
  'D-37: plate uniqueness over PENDING/APPROVED only.';
