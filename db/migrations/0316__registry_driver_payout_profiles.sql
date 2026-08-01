-- =====================================================================================
-- 0316 — registry: the driver's bank & payout profile, and their own LankaQR
-- Source: specs/architecture-design-document.md §1.18 (AL-58, AL-59) · §11.9a
--         specs/D3_mageride_api_contracts.md registry-svc route table
--         backend/contracts/registry.yaml getDriverPayoutProfile / upsertDriverPayoutProfile
--         specs/server_db_schema.md §2 · D4' §2
--
-- AL-58/AL-59. **Additive only** — see the note at the bottom about `registry.driver_payouts`.
--
-- WHY THIS TABLE EXISTS. AL-57 retired D-11: OnePay supports one merchant account per merchant,
-- so the per-driver merchant sub-account a card fare was supposed to land in never existed. Card
-- fares now land in MageRide's account by way of a passenger wallet, which makes a driver's wallet
-- balance **money the platform owes** — and AL-05 had already deleted the only outward bank rail.
-- This is where the platform learns where to send it.
--
-- SHAPED EXACTLY LIKE registry.fleet_payout_profiles (0301 + 0313), deliberately. The platform
-- must not hold a payee's bank details in two shapes: an operator reading a fleet's account and a
-- driver's should be reading the same columns, the officer approving them uses the same queue, and
-- subscription-svc's and payout-svc's "find the one verified row" reads are the same query with a
-- different owner column. Every rule 0313 argued applies here for the same reason:
--
--   * **Versioned, not updated in place.** An edit INSERTs a new `pending_verification` row and
--     leaves the incumbent `verified` and collecting, so a driver who mistypes an account number
--     on Friday is still paid on Sunday against the account an officer approved.
--   * **`superseded` is the status the incumbent moves to** when a later edit is approved in its
--     place — `rejected` is a decision nobody took and `pending_verification` would put a
--     superseded row back in the queue. The partial unique index is what makes the ORDER of the
--     officer's two writes matter.
--   * **`proof_upload_id` carries either a bank statement or a passbook first page.** BR-31.1 asks
--     for one *or* the other; one column is what makes "replace the blurred photograph" work.
--
-- ONE THING IS NEW HERE AND IS NOT ON THE FLEET'S TABLE'S CRITICAL PATH: `lankaqr_upload_id` is
-- load-bearing for **every ride**, not only for a payout. AL-59 makes a LankaQR ride payment the
-- driver's OWN bank QR — the passenger scans this image — because the platform-merchant `lankaqr`
-- rail was collecting fares into MageRide's account while crediting the driver nothing but a
-- read-model row. A driver with no QR here simply cannot be paid by that rail; cash and the wallet
-- remain.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.driver_payout_profiles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  bank TEXT NOT NULL,
  branch TEXT NOT NULL,
  account_no TEXT NOT NULL,
  account_holder_name TEXT NOT NULL,
  -- FKs onto docs.uploads(id) are DEFERRED to 1313: `docs` is created by C005's 1301 and this
  -- script is in the 03xx registry range, which runs first. Exactly the arrangement C003 made for
  -- registry.fleet_payout_profiles (0301 + 1301's tail) and for the same reason — the range is by
  -- schema, and a table does not move ranges to chase a foreign key.
  -- bank_statement | passbook_first_page — one column, because BR-31.1 asks for one or the other.
  proof_upload_id UUID,
  -- AL-59: the driver's own bank-app LankaQR. Read on the ride pay sheet, not only at payout time.
  lankaqr_upload_id UUID,
  status TEXT NOT NULL DEFAULT 'pending_verification'
    CONSTRAINT ck_driver_payout_status
    CHECK (status IN ('pending_verification','verified','rejected','superseded')),
  rejection_reason TEXT,
  verified_by UUID REFERENCES iam.users(id),
  verified_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- At most one live verified profile per driver — the row the weekly payout run pays and the ride
-- pay sheet renders. An approval must supersede the incumbent in the SAME transaction as it
-- verifies the replacement, or the second write fails here. Said out loud rather than discovered
-- from a 23505, exactly as ux_payout_profile_verified says it for a fleet.
CREATE UNIQUE INDEX IF NOT EXISTS ux_driver_payout_verified
  ON registry.driver_payout_profiles(driver_id) WHERE status = 'verified';
CREATE INDEX IF NOT EXISTS ix_driver_payout_driver
  ON registry.driver_payout_profiles(driver_id);
-- The Verification Officer's queue reads this predicate (AL-39, SCR-AP-003).
CREATE INDEX IF NOT EXISTS ix_driver_payout_pending
  ON registry.driver_payout_profiles(created_at) WHERE status = 'pending_verification';

SELECT public.attach_set_updated_at('registry','driver_payout_profiles');

COMMENT ON TABLE registry.driver_payout_profiles IS
  'Versioned driver bank & payout profile (AL-58). Verification-Officer approved through the AL-39 queue, whose routes are subject-agnostic and already take a driver id. The weekly payout run (AL-58) pays the single verified row; a driver with no verified row accrues on their wallet and is never swept — the balance is retained, never lost.';
COMMENT ON COLUMN registry.driver_payout_profiles.lankaqr_upload_id IS
  'AL-59: the driver''s OWN bank-app LankaQR image. What a passenger scans to pay them (scan_driver_qr) — the money moves bank-to-bank and never passes through MageRide, which is why that rail settles by AL-47 attestation and has no webhook.';
COMMENT ON INDEX registry.ux_driver_payout_verified IS
  'One verified payout profile per driver (AL-58). Supersede the incumbent in the same transaction as the replacement is verified, or the second write fails here.';

-- -------------------------------------------------------------------------------------
-- registry.driver_payouts is NOT dropped here, on purpose.
-- -------------------------------------------------------------------------------------
-- D-11 is retired (AL-57) and that table has no future, but registry-svc (SubscriptionRepository,
-- MerchantService) and fare-svc (FareGateways) still read and write it, and both are DONE
-- components with green suites. DbUp applies this directory to every environment including the
-- replica; dropping a table two running services still name would take them down between this
-- migration and the code change that stops referencing it.
--
-- The drop ships with the component that removes the LAST reference — C050 (fare-svc-payments) per
-- the AL-57 change set — as its own numbered script, because a released script is immutable and a
-- correction is a new file. Same reason the `fares.ride_payments.method` CHECK is widened in 1007
-- and narrowed later rather than rewritten now.
