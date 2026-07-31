-- =====================================================================================
-- 1310 — docs.extractions: the ADD §12.5 document-processing log (C054, ocr-svc)
-- Source: architecture-design-document.md §12.5 · D6' §7.5 · D-36 · NFR-28
--
-- §12.5 asks for one artefact this build has nowhere to put:
--
--     "Document processing log: hash + policy version + redaction-pass version stored
--      per extraction."
--
-- 1301's docs.extractions carries a single BOOLEAN, `redaction_applied`, which can say
-- *that* a document was redacted and nothing about *how*. That is not enough to answer the
-- question a privacy impact assessment asks (§12.5's last line makes one a precondition of
-- production rollout): "this driver's licence was processed on the 3rd — which file was it,
-- what did the pass mask that day, and which build of the pass did the masking?" A policy
-- change — adding a new identifier family, widening a mask — is invisible without a version
-- on the row, so a regression could not be scoped to the extractions it affected.
--
-- The hash is of BOTH images. The raw hash identifies the file without keeping it, which is
-- what makes the log survive NFR-28's 90-day deletion; the redacted hash is what left the
-- perimeter, and is the value ocr-svc's PerimeterGuardHandler admits.
--
-- Also here: `engine`, because D6' §7.5 has two extractors and §8.3 has a documented
-- fallback between them, and a row that cannot say which one produced it cannot answer
-- "how much of the officer queue is Gemini being down" — the operational question the
-- fallback exists to create.
--
-- Raised as a micro-change-set in the C054 handoff. D4' §11-16 / server_db_schema §12
-- should carry these columns.
-- =====================================================================================

ALTER TABLE docs.extractions
  -- ADD §12.5 "hash": sha256 of the bytes as they came off object storage. Hex, not BYTEA,
  -- because it is compared with values ocr-svc computes in-process and read by humans in a
  -- support ticket far more often than it is joined on.
  ADD COLUMN IF NOT EXISTS raw_sha256 TEXT,
  -- The image that actually left the perimeter. NULL on a Tesseract-only extraction, where
  -- nothing did.
  ADD COLUMN IF NOT EXISTS redacted_sha256 TEXT,
  -- ADD §12.5 "policy version": WHAT the pass masks. Bumped when the set changes.
  ADD COLUMN IF NOT EXISTS redaction_policy_version TEXT,
  -- ADD §12.5 "redaction-pass version": WHICH BUILD masked it. Bumped when the
  -- implementation changes without the policy.
  ADD COLUMN IF NOT EXISTS redaction_pass_version TEXT,
  -- How much the pass found. Zero faces on an insurance certificate is correct; zero faces
  -- on every driving licence for a week is a broken cascade, and without the count that is
  -- indistinguishable from a fleet of documents with no portraits on them.
  ADD COLUMN IF NOT EXISTS faces_blurred SMALLINT,
  ADD COLUMN IF NOT EXISTS identifiers_masked SMALLINT,
  -- D6' §7.5's two extractors, plus 'none' for a document neither could read.
  ADD COLUMN IF NOT EXISTS engine TEXT;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'docs.extractions'::regclass
                    AND conname = 'ck_extractions_engine') THEN
    ALTER TABLE docs.extractions
      ADD CONSTRAINT ck_extractions_engine
      CHECK (engine IS NULL OR engine IN ('gemini','tesseract','none'));
  END IF;

  -- The invariant D-36 is: an image only leaves the perimeter redacted. A row claiming the
  -- external model ran with `redaction_applied = false` describes the one thing that must
  -- never have happened, and the database is the last place able to refuse to record it.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'docs.extractions'::regclass
                    AND conname = 'ck_extractions_gemini_is_redacted') THEN
    ALTER TABLE docs.extractions
      ADD CONSTRAINT ck_extractions_gemini_is_redacted
      CHECK (engine IS DISTINCT FROM 'gemini' OR redaction_applied)
      NOT VALID;   -- pre-C054 rows carry no engine at all and are not in scope.
  END IF;
END $$;

-- The operational question the fallback creates: how much of the officer queue is Gemini
-- being down? Partial, so the index is the size of the fallback traffic rather than of the
-- whole extraction history.
CREATE INDEX IF NOT EXISTS ix_extractions_fallback
  ON docs.extractions(created_at) WHERE engine = 'tesseract';

COMMENT ON COLUMN docs.extractions.raw_sha256 IS
  'ADD §12.5 processing log: sha256 of the raw upload. Identifies the file after NFR-28 deletes it.';
COMMENT ON COLUMN docs.extractions.redacted_sha256 IS
  'sha256 of the image that left the perimeter. NULL when nothing did (on-prem Tesseract path).';
COMMENT ON COLUMN docs.extractions.redaction_policy_version IS
  'ADD §12.5: WHAT the D-36 pre-pass masked (the set of identifier families and region types).';
COMMENT ON COLUMN docs.extractions.redaction_pass_version IS
  'ADD §12.5: WHICH BUILD of the pre-pass masked it.';
COMMENT ON COLUMN docs.extractions.engine IS
  'D6'' §7.5: gemini (primary, redacted image) | tesseract (on-prem fallback) | none (neither could read it).';
