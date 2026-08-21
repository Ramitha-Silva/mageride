-- =====================================================================================
-- 1315 — docs.extractions: the D-36 pre-pass stops being a precondition of the model
-- Source: Δ MCS-07 · supersedes part of 1310 · D6' §7.5 · ADD §12.5 · D-36
--
-- 1310 added `ck_extractions_gemini_is_redacted`:
--
--     CHECK (engine IS DISTINCT FROM 'gemini' OR redaction_applied)
--
-- and described it as "the D-36 invariant in the last place able to refuse to record its
-- violation". That was accurate while ocr-svc could not physically make the call without a
-- redacted image: `GeminiFieldExtractor` took a `RedactedDocument`, a type only the redaction
-- pipeline could construct, so a row of that shape could only be produced by a defect.
--
-- MCS-07 changed the service's posture by decision: the pre-pass now runs when it can and is
-- SKIPPED when it cannot, and the image is sent either way. The chain it used to be part of --
-- no tesseract binary => no ID-number boxes => no redaction => no Gemini => no extraction at
-- all -- meant a deployment missing a native dependency read nothing by any path while
-- looking, from the outside, exactly like a working one. That was the whole of the observed
-- defect: every field blank on both onboarding surfaces.
--
-- So `engine = 'gemini' AND redaction_applied = false` is no longer the thing that must never
-- have happened. It is an ordinary, expected row, and the constraint would now reject the
-- audit record of the very extractions most worth auditing -- which `ExtractionPipeline`
-- swallows as a logged NpgsqlException, losing the row and keeping the extraction. A
-- constraint that turns a policy change into silent gaps in the processing log is worse than
-- no constraint.
--
-- WHAT IS NOT DROPPED, and is now the only record of the fact:
--   * `redaction_applied` still says, per row, whether that image left masked.
--   * `redacted_sha256`, `redaction_policy_version`, `redaction_pass_version`, `faces_blurred`
--     and `identifiers_masked` are still written whenever the pass ran, and are still NULL
--     when it did not -- so the two populations are separable in one query.
--   * `raw_sha256` is now written on EVERY extraction rather than only on redacted ones
--     (ocr-svc's `PersistAsync` takes it off the outbound document), because ADD §12.5's
--     "which file was processed" is the one column a privacy impact assessment cannot do
--     without, and it was previously absent from exactly the rows that need it most.
--
-- ADD §12.5 makes a privacy impact assessment a precondition of production rollout. This
-- migration does not perform one; it records that the invariant it asserted is no longer held
-- by the code, so the assessment is answering a question about real behaviour.
--
-- The count of affected rows is deliberately reported rather than assumed -- on the replica it
-- is zero, because nothing has ever successfully reached Gemini there.
-- =====================================================================================

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_constraint
              WHERE conrelid = 'docs.extractions'::regclass
                AND conname = 'ck_extractions_gemini_is_redacted') THEN
    ALTER TABLE docs.extractions DROP CONSTRAINT ck_extractions_gemini_is_redacted;

    RAISE NOTICE 'MCS-07: dropped ck_extractions_gemini_is_redacted; unredacted Gemini extractions are now recordable.';
  END IF;
END $$;

-- The fallback-volume question 1310's partial index answers has a sibling now: how much of the
-- traffic left the perimeter unmasked? Partial for the same reason -- the index is the size of
-- the unredacted population, which should be small and bounded by a missing dependency rather
-- than growing with the whole extraction history.
CREATE INDEX IF NOT EXISTS ix_extractions_unredacted
  ON docs.extractions(created_at) WHERE engine = 'gemini' AND NOT redaction_applied;

COMMENT ON COLUMN docs.extractions.redaction_applied IS
  'Whether the D-36 pre-pass masked this image before it left. Since MCS-07 the pass is best-effort, so false is an expected value on an engine=''gemini'' row: this column — no longer a CHECK constraint — is what says which documents left unmasked, and ix_extractions_unredacted indexes exactly those.';
