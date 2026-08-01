-- =====================================================================================
-- 1313 — docs: the deferred foreign keys from registry.driver_payout_profiles
-- Source: specs/architecture-design-document.md §1.18 (AL-58, AL-59)
--
-- 0316 created `registry.driver_payout_profiles` with `proof_upload_id` and `lankaqr_upload_id`
-- bare, because `docs.uploads` is C005's (1301) and the 03xx registry range applies before the
-- 13xx one. This is the tail that closes them — the same arrangement C003 made for
-- `registry.fleet_payout_profiles`, whose two upload FKs are added at the bottom of 1301.
--
-- Guarded by name rather than by `IF NOT EXISTS`, which ALTER TABLE ... ADD CONSTRAINT does not
-- have, so the script survives migrate-verify.sh's third pass with the journal disabled.
-- =====================================================================================

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.driver_payout_profiles'::regclass
                    AND conname = 'fk_driver_payout_proof_upload') THEN
    ALTER TABLE registry.driver_payout_profiles
      ADD CONSTRAINT fk_driver_payout_proof_upload
      FOREIGN KEY (proof_upload_id) REFERENCES docs.uploads(id);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.driver_payout_profiles'::regclass
                    AND conname = 'fk_driver_payout_lankaqr_upload') THEN
    ALTER TABLE registry.driver_payout_profiles
      ADD CONSTRAINT fk_driver_payout_lankaqr_upload
      FOREIGN KEY (lankaqr_upload_id) REFERENCES docs.uploads(id);
  END IF;
END $$;
