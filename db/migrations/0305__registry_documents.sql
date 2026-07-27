-- =====================================================================================
-- 0305 — registry: documents, per-field verification, onboarding state machine
-- Source: server_db_schema.md §2, §26 · D4' §2 (incl. the 2026-06-25 AL-29/AL-30 pass)
--         · AL-10, AL-29, AL-30, AL-50, E-03
-- =====================================================================================

-- Document expiry tracking (E-03). Two owners are possible: a driver uploading their own
-- documents (Mode C), or a fleet uploading a vehicle's (SCR-FP-004, AL-50).
CREATE TABLE IF NOT EXISTS registry.documents (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- AL-50 made driver_id nullable so a fleet can own the row.
  driver_id UUID REFERENCES iam.users(id) ON DELETE CASCADE,
  fleet_id UUID REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  -- 'revenue_license' comes from D4' §2; server_db_schema.md §2 omits it although AL-50 names
  -- it as one of the four SCR-FP-004 slots. See the C003 handoff note.
  kind TEXT NOT NULL CHECK (kind IN
    ('driving_license','registration','permit','insurance','revenue_license')),
  file_url TEXT NOT NULL,
  issued_at TIMESTAMPTZ,
  expires_at TIMESTAMPTZ,
  status TEXT NOT NULL DEFAULT 'VALID'
    CHECK (status IN ('VALID','EXPIRING','EXPIRED','REJECTED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- AL-50 "exactly the uploading principal". The spec's printed DDL writes OR (at least one);
  -- its own comment and the C003 definition of done both say exactly one, so this is XOR.
  CONSTRAINT ck_documents_owner CHECK (num_nonnulls(driver_id, fleet_id) = 1));

CREATE INDEX IF NOT EXISTS ix_documents_expiry
  ON registry.documents(expires_at) WHERE status <> 'EXPIRED';   -- E-03 nightly job
CREATE INDEX IF NOT EXISTS ix_documents_driver ON registry.documents(driver_id);
CREATE INDEX IF NOT EXISTS ix_documents_fleet ON registry.documents(fleet_id);
CREATE INDEX IF NOT EXISTS ix_documents_vehicle ON registry.documents(vehicle_id);

SELECT public.attach_set_updated_at('registry','documents');

COMMENT ON TABLE registry.documents IS
  'Uploaded driver/vehicle documents with expiry (E-03). AL-10 approval gate: verified registration + insurance + revenue_license for all modes, plus a verified permit for Mode A, before registry.vehicles.status can reach APPROVED. Expiry auto-suspends dispatch.';
COMMENT ON COLUMN registry.documents.vehicle_id IS
  'NULL for driver-identity documents: kind=''driving_license'' is captured at Profile Setup and is vehicle-less.';

-- Provenance and verification of every extracted or typed field (AL-29; US-2.4a/2.10a).
-- A field is 'pending' when it was typed by hand, when OCR confidence is below threshold,
-- or when the photos step's plate OCR disagrees with the entered registration number.
CREATE TABLE IF NOT EXISTS registry.document_fields (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id UUID NOT NULL REFERENCES registry.documents(id) ON DELETE CASCADE,
  field_key TEXT NOT NULL,                                    -- licence_no | licence_expiry | nic_no | allowed_vehicle_types | insurance_expiry | revenue_no | revenue_expiry | reg_no_match | ...
  field_value TEXT,
  confidence NUMERIC(4,3) CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  source TEXT NOT NULL DEFAULT 'ai' CHECK (source IN ('ai','manual')),
  verify_status TEXT NOT NULL DEFAULT 'auto_verified'
    CHECK (verify_status IN ('auto_verified','pending','confirmed')),
  confirmed_by UUID REFERENCES iam.users(id),
  confirmed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- confidence is meaningless for a hand-typed value (D4' §2: "NULL when source='manual'").
  CONSTRAINT ck_document_fields_manual_confidence
    CHECK (source <> 'manual' OR confidence IS NULL));

CREATE INDEX IF NOT EXISTS ix_document_fields_document ON registry.document_fields(document_id);
CREATE INDEX IF NOT EXISTS ix_document_fields_pending
  ON registry.document_fields(document_id) WHERE verify_status = 'pending';

COMMENT ON INDEX registry.ix_document_fields_pending IS
  'AL-29: drives the Verification-Officer queue (SCR-AP-003). Nothing reaches APPROVED while a field is pending.';

-- Persisted per-step Mode-C onboarding state machine (AL-30; US-2.10a/2.26/2.27). Each step
-- is saved individually; re-opening the wizard resumes at the first non-verified step, and
-- registry.vehicles.onboarding_status is derived from these four rows.
CREATE TABLE IF NOT EXISTS registry.onboarding_steps (
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  step TEXT NOT NULL CHECK (step IN ('details','insurance','revenue','photos')),
  status TEXT NOT NULL DEFAULT 'pending_input'
    CHECK (status IN ('pending_input','verified','pending_review')),
  fields JSONB,
  saved_at TIMESTAMPTZ,
  PRIMARY KEY (vehicle_id, step));

CREATE INDEX IF NOT EXISTS ix_onboarding_steps_review
  ON registry.onboarding_steps(vehicle_id) WHERE status = 'pending_review';

COMMENT ON TABLE registry.onboarding_steps IS
  'AL-30: four-step Mode-C vehicle onboarding. pending_review when any of the step''s registry.document_fields is pending, or (photos) when plate OCR does not match registration_number.';
