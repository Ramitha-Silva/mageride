-- =====================================================================================
-- 0301 — registry: fleet organisations and payout profiles
-- Source: server_db_schema.md §2, §26 · D4' §2, Δ 2026-07-18 · AL-03, AL-49
--
-- Runs before the rest of registry because iam.fleet_members (0302) and
-- registry.documents (0305) both reference registry.fleets.
-- =====================================================================================

-- Fleet Owner organisation (AL-03, Epic 13). Verification-Officer gated: nothing operates
-- until status='APPROVED'.
CREATE TABLE IF NOT EXISTS registry.fleets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),            -- the fleet_owner primary account
  name TEXT NOT NULL,
  business_reg TEXT,
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED')),
  rejection_reason TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_fleets_owner ON registry.fleets(owner_id);
CREATE INDEX IF NOT EXISTS ix_fleets_status ON registry.fleets(status) WHERE status = 'PENDING';

SELECT public.attach_set_updated_at('registry','fleets');

-- Org bank and payout profile (AL-49, SCR-FP-002a) — receives Mode B pass-through payments
-- (BR-23.10). Edits INSERT a new row and re-verify, so the table is versioned; the pay
-- sheet's payTo always reads the single 'verified' row.
CREATE TABLE IF NOT EXISTS registry.fleet_payout_profiles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  bank TEXT NOT NULL,
  branch TEXT NOT NULL,
  account_no TEXT NOT NULL,
  account_holder_name TEXT NOT NULL,
  -- FKs to docs.uploads(id) are deferred: docs.uploads is created by C005. The constraints
  -- are added by the C005 docs migration; see the C003 handoff note.
  proof_upload_id UUID,                                       -- bank_statement | passbook_first_page
  lankaqr_upload_id UUID,                                     -- bank-app-generated LankaQR image
  status TEXT NOT NULL DEFAULT 'pending_verification'
    CHECK (status IN ('pending_verification','verified','rejected')),
  rejection_reason TEXT,
  verified_by UUID REFERENCES iam.users(id),
  verified_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- At most one live verified profile per org (BR-31.1).
CREATE UNIQUE INDEX IF NOT EXISTS ux_payout_profile_verified
  ON registry.fleet_payout_profiles(fleet_id) WHERE status = 'verified';
CREATE INDEX IF NOT EXISTS ix_payout_profile_fleet ON registry.fleet_payout_profiles(fleet_id);

SELECT public.attach_set_updated_at('registry','fleet_payout_profiles');

COMMENT ON TABLE registry.fleet_payout_profiles IS
  'Versioned org bank/payout profile (AL-49). AL-49 BR-31.1: mode_b_billing=''paid'' requires a verified row here.';
