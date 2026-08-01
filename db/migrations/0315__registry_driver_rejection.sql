-- =====================================================================================
-- 0315 — registry: the reason a Verification Officer refused a driver's identity submission
-- Source: backend/contracts/admin-bff.yaml rejectVerificationSubject · D3' Δ 2026-06-28 item 8
--         (AL-39) · US-2.4a / US-2.15 · server_db_schema.md §2 · D4' §2
--
-- C063 (admin-bff-verification). One column, one spec gap raised in the C063 handoff.
--
-- ⚠ A REJECTED DRIVER HAS NOWHERE TO PUT THE REASON — micro-change-set.
--     AL-39 makes the verification family **subject-agnostic**: `POST
--     /admin/verification/{subjectId}/reject` takes a driver id, a vehicle id or a fleet-org id,
--     and US-2.15 makes the reason mandatory and "surfaced verbatim to the applicant". Two of the
--     three subjects already have somewhere to keep it — `registry.vehicles.rejection_reason`
--     (0303) and `registry.fleets.rejection_reason` (0301) — and the third does not:
--     `registry.driver_profiles` (0304) carries `verified_at` and nothing else, so the officer's
--     decision on a driving licence could be recorded in `audit.events` and never shown to the
--     driver it was about. AL-29 put driver-identity fields (`nic_no`, `allowed_vehicle_types`)
--     into the same Verification-Officer queue as a vehicle's; the refusal path has to match.
--     **server_db_schema.md §2 / D4' §2 should carry this column on `registry.driver_profiles`.**
--
-- Shaped exactly like `registry.vehicles.rejection_reason`: TEXT, nullable, no companion
-- timestamp. `updated_at` already moves when the row is written (0304 attaches the trigger) and a
-- second "when" column would be a fact two places could disagree about; **who** decided is the
-- `audit.events` row admin-bff writes in the same transaction (D-35), which is the record an
-- auditor reads and the one that survives the profile being re-submitted.
--
-- There is deliberately no `status` column. A driver's verification state is derived, exactly as
-- `registry.vehicles.onboarding_status` is: verified_at set => APPROVED, else rejection_reason set
-- => REJECTED, else PENDING. A stored status would be a second opinion about the same two columns,
-- and the queue reads it from the fields (`registry.document_fields.verify_status = 'pending'`)
-- rather than from either.
-- =====================================================================================

ALTER TABLE registry.driver_profiles ADD COLUMN IF NOT EXISTS rejection_reason TEXT;

COMMENT ON COLUMN registry.driver_profiles.rejection_reason IS
  'US-2.15 applied to the driver subject of AL-39''s verification family: why a Verification Officer refused this identity submission, shown verbatim to the driver. Cleared when the profile is approved. Derived state: verified_at => APPROVED, else this column => REJECTED, else PENDING (C063 gap).';
