-- =====================================================================================
-- 1301 — docs: uploads and OCR extractions
-- Source: server_db_schema.md §12 + §23 (Δ 2026-06-28) + §26 (Δ 2026-07-18)
--         D4' §11-16 + Δ 2026-06-28 + Δ 2026-07-18 · ADD §9.1 · D-36, AL-43, AL-49, NFR-28
--
-- Ordered first in the 13xx range because C003 deferred two FK constraints to this file:
-- registry.fleet_payout_profiles.proof_upload_id / lankaqr_upload_id (see the C003 handoff,
-- decision 7). They are added at the bottom.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS docs.uploads (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),
  storage_url TEXT NOT NULL,
  sha256 BYTEA,
  -- Free text in both specs, deliberately un-CHECKed: the set grows with every onboarding
  -- surface. Known values are driving_license, registration, insurance, revenue_license,
  -- permit, vehicle_photo (registry), and bank_statement, passbook_first_page,
  -- lankaqr_code (AL-49, SCR-FP-002a).
  kind TEXT,
  -- AL-43: whether the image came from the in-app drag-crop scanner or the gallery. A
  -- gallery upload is the fraud signal the verification queue sorts on.
  captured_via TEXT CONSTRAINT ck_uploads_captured_via
    CHECK (captured_via IN ('camera_dragcrop','gallery','other')),
  -- NFR-28: raw documents are deleted 90 days after capture; the extraction survives.
  auto_delete_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_uploads_owner ON docs.uploads(owner_id, created_at DESC);
-- The NFR-28 sweeper scans by deadline.
CREATE INDEX IF NOT EXISTS ix_uploads_auto_delete
  ON docs.uploads(auto_delete_at) WHERE auto_delete_at IS NOT NULL;

COMMENT ON TABLE docs.uploads IS
  'Object-storage pointers for every uploaded document and photo (D-36). The bytes live on SSE-KMS storage, never in Postgres; raw files auto-delete after 90 days (NFR-28).';
COMMENT ON COLUMN docs.uploads.captured_via IS
  'AL-43 capture provenance. NULL for historical rows; new onboarding captures set camera_dragcrop.';

CREATE TABLE IF NOT EXISTS docs.extractions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  upload_id UUID NOT NULL REFERENCES docs.uploads(id) ON DELETE CASCADE,
  doc_type TEXT NOT NULL,
  extracted JSONB,
  confidence NUMERIC CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
  status TEXT NOT NULL DEFAULT 'PENDING' CONSTRAINT ck_extractions_status
    CHECK (status IN ('PENDING','EXTRACTED','MANUAL_REVIEW','FAILED')),
  -- D-36: PII is redacted (face blur + ID mask) BEFORE the image reaches Gemini. Defaults
  -- true so a row that forgets to set it reads as compliant only when it actually is —
  -- ocr-svc (C054) must set false explicitly on any path that skipped the pre-pass.
  redaction_applied BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_extractions_upload ON docs.extractions(upload_id);
-- The Verification Officer manual-review queue (US-2.10).
CREATE INDEX IF NOT EXISTS ix_extractions_review
  ON docs.extractions(created_at) WHERE status = 'MANUAL_REVIEW';

COMMENT ON TABLE docs.extractions IS
  'One row per OCR pass (D-36). Gemini Flash 3.0 primary, Tesseract fallback, manual review below threshold (D6 §7.5).';

-- -------------------------------------------------------------------------------------
-- C003 deferred FKs (AL-49). registry.fleet_payout_profiles was created in 0301 with both
-- upload columns bare because docs.uploads did not exist yet. Added here, guarded so the
-- script stays re-runnable under migrate-verify.sh pass 3.
-- -------------------------------------------------------------------------------------
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.fleet_payout_profiles'::regclass
                    AND conname = 'fk_payout_profiles_proof_upload') THEN
    ALTER TABLE registry.fleet_payout_profiles
      ADD CONSTRAINT fk_payout_profiles_proof_upload
      FOREIGN KEY (proof_upload_id) REFERENCES docs.uploads(id);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.fleet_payout_profiles'::regclass
                    AND conname = 'fk_payout_profiles_lankaqr_upload') THEN
    ALTER TABLE registry.fleet_payout_profiles
      ADD CONSTRAINT fk_payout_profiles_lankaqr_upload
      FOREIGN KEY (lankaqr_upload_id) REFERENCES docs.uploads(id);
  END IF;
END $$;

COMMENT ON COLUMN registry.fleet_payout_profiles.proof_upload_id IS
  'bank_statement | passbook_first_page (AL-49). FK added by the C005 docs migration, which creates the target.';
COMMENT ON COLUMN registry.fleet_payout_profiles.lankaqr_upload_id IS
  'Bank-app-generated LankaQR image (AL-49), served to the passenger pay sheet as a signed URL.';
