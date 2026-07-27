-- =====================================================================================
-- 0304 — registry: driver profiles and payout bindings
-- Source: server_db_schema.md §2 · D4' §2 · AL-29, D-11
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.driver_profiles (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id) ON DELETE CASCADE,
  display_name TEXT NOT NULL,
  photo_url TEXT,
  verified_at TIMESTAMPTZ,
  -- AL-29: extracted from the driving-licence scan, or typed by the driver when the scan is
  -- unclear. Provenance and per-field verification live in registry.document_fields.
  nic_no TEXT,
  allowed_vehicle_types TEXT[],                               -- licence classes (US-2.4a)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('registry','driver_profiles');

COMMENT ON COLUMN registry.driver_profiles.nic_no IS
  'AL-29. PII: the NIC number is masked before the document image leaves the perimeter (D-36); the value is captured from the structured OCR response.';

-- OnePay merchant binding (D-11): where a driver''s QR/card settlements land.
CREATE TABLE IF NOT EXISTS registry.driver_payouts (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id) ON DELETE CASCADE,
  onepay_merchant_id TEXT NOT NULL,
  bound_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')));
