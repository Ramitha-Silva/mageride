-- =====================================================================================
-- 0313 — registry: fleet-org KYC contact, business-registration uniqueness, the R-14
--        command log, and the payout-profile status a supersede needs
-- Source: backend/contracts/fleet.yaml registerFleet / getFleet / upsertPayoutProfile ·
--         D3' "fleet-svc (Phase 1, AL-03)" + Δ 2026-07-18 item 1 · US-13.A7 · BR-31.1
--         · server_db_schema.md §2, §26 · AL-03, AL-49
--
-- C058 (fleet-svc-org). Four changes, three of them spec gaps raised in the C058 handoff.
--
-- (a) ⚠ THE ORG HAS NOWHERE TO PUT ITS KYC CONTACT — micro-change-set.
--     `POST /v1/fleets` requires `{name, registrationNo, contactPhone}` and returns
--     `contactEmail` and `address`, and US-13.A7 makes the submission "organisation KYC
--     (business name/registration, contact, authorised-person ID)" — the thing a
--     Verification Officer reads before approving. `registry.fleets` (§2, migration 0301)
--     carries `name` and `business_reg` and nothing else, so two required contract fields
--     and the whole of the officer's contact evidence have no column. Recording them on
--     `iam.users` instead would be wrong twice over: the contact is the organisation's, not
--     the signing-in person's, and an owner who changes their own phone number would
--     silently rewrite a KYC record an officer already approved.
--     **server_db_schema.md §2 / D4' §2 should carry these three columns.**
--
-- (b) ⚠ A VERIFIED PAYOUT PROFILE CANNOT BE REPLACED — micro-change-set.
--     §26 states two rules that cannot both hold under the printed CHECK:
--       * "Edits INSERT a new row + re-verify (versioned)", and BR-31.1's "Paid
--         subscriptions keep collecting against the last verified snapshot" — so the old
--         row must STAY `verified` while the edit sits at `pending_verification`; and
--       * `ux_payout_profile_verified`, "at most one live verified profile per org".
--     Both hold right up to the moment the officer approves the edit, and then there is no
--     legal status left to move the old row to: `rejected` is a decision nobody took, and
--     `pending_verification` would put a superseded row back in the queue. `superseded` is
--     added for exactly that transition. Nothing else changes — subscription-svc's pay-sheet
--     read (`WHERE status = 'verified'`, C050) is untouched and still finds exactly one row.
--     **server_db_schema.md §26 / D4' Δ 2026-07-18 should carry the fourth value.**
--
-- (c) ⚠ NO COMMAND LOG FOR fleet-svc — micro-change-set, the twelfth instance of the same
--     one (iam 0104, registry 0307, prov 0402, trips 0505, rides 0603, dispatch 0710,
--     reputation 0803, fares 1005, subscription 1203, content 1307, comms 1308,
--     transit 1407). D4' §5 prints command-log DDL for `rides` only while D3' §0 makes
--     `Idempotency-Key` mandatory on every POST mutation, and `fleet.yaml` declares the
--     header on all four of this service's POSTs.
--     **D4' §5 should carry a command log per bounded context.**
--
-- (d) Business-registration uniqueness. Not a gap — a rule with no home. Two orgs claiming
--     one business registration is the KYC failure the officer queue exists to catch, and
--     catching it at submit is cheaper than catching it twice in the queue. Shaped exactly
--     like D-37's `ux_vehicles_regno_active`: the live set only, so a REJECTED org's number
--     is free again.
--
-- fleet-svc's tables live in `registry` because `registry.fleets` does (0301). There is no
-- `fleet` schema in 0001 and this migration does not add one — an org is a registry concept
-- and the two services are separated by ownership of rows, not by schema.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (a) KYC contact
-- -------------------------------------------------------------------------------------

ALTER TABLE registry.fleets ADD COLUMN IF NOT EXISTS contact_phone TEXT;
ALTER TABLE registry.fleets ADD COLUMN IF NOT EXISTS contact_email TEXT;
ALTER TABLE registry.fleets ADD COLUMN IF NOT EXISTS address TEXT;

COMMENT ON COLUMN registry.fleets.contact_phone IS
  'Organisation contact in E.164 (+947XXXXXXXX), required by POST /v1/fleets. The org''s number, not the signing-in owner''s — an owner changing their own phone must not rewrite approved KYC (C058 gap (a)).';
COMMENT ON COLUMN registry.fleets.contact_email IS
  'Optional organisation contact address. Distinct from iam.users.email, which is a sign-in credential (AL-07).';
COMMENT ON COLUMN registry.fleets.address IS
  'Registered business address, shown to the Verification Officer in the fleet-org queue (US-13.A7, SCR-AP-003).';

-- -------------------------------------------------------------------------------------
-- (d) One live organisation per business registration
-- -------------------------------------------------------------------------------------

-- lower() so "PV 12345" and "pv 12345" collide; the live set only, so a rejected
-- application does not permanently burn the number it quoted (D-37's shape).
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleets_business_reg_active
  ON registry.fleets(lower(business_reg))
  WHERE business_reg IS NOT NULL AND status IN ('PENDING','APPROVED');

COMMENT ON INDEX registry.ux_fleets_business_reg_active IS
  'One live organisation per business registration. Live = PENDING or APPROVED, so a REJECTED application frees the number (same shape as D-37''s ux_vehicles_regno_active).';

-- -------------------------------------------------------------------------------------
-- (b) `superseded` — the status a replaced payout profile moves to
-- -------------------------------------------------------------------------------------

-- 0301 declared the CHECK inline, so it is system-named. Dropped by both the system name and
-- the explicit one, so this script is re-runnable and a later change has a name to target.
ALTER TABLE registry.fleet_payout_profiles DROP CONSTRAINT IF EXISTS fleet_payout_profiles_status_check;
ALTER TABLE registry.fleet_payout_profiles DROP CONSTRAINT IF EXISTS ck_payout_profile_status;
ALTER TABLE registry.fleet_payout_profiles
  ADD CONSTRAINT ck_payout_profile_status
  CHECK (status IN ('pending_verification','verified','rejected','superseded'));

COMMENT ON COLUMN registry.fleet_payout_profiles.status IS
  'pending_verification -> verified | rejected, and verified -> superseded when a later edit is approved in its place (C058 gap (b)). ux_payout_profile_verified keeps exactly one verified row; the pay sheet reads that one and never an unverified edit (BR-31.1).';

-- The officer's decision is one statement over two rows — supersede the old, verify the new
-- — and the unique index is what makes the order matter. Documented on the index rather than
-- discovered from a 23505.
COMMENT ON INDEX registry.ux_payout_profile_verified IS
  'At most one verified payout profile per org (BR-31.1). An approval must supersede the incumbent in the same transaction as it verifies the replacement, or the second write fails on this index.';

-- -------------------------------------------------------------------------------------
-- (c) R-14 command log
-- -------------------------------------------------------------------------------------

-- Shape is 1407 exactly (0603 minus the aggregate-id column). fleet-svc's idempotent POSTs
-- are `POST /v1/fleets`, `POST /v1/fleets/{id}/members`,
-- `POST /v1/fleets/{id}/payout-profile/documents` and the internal approve/reject — and the
-- first of them is the one that matters most: a double-submitted registration with no replay
-- puts a second organisation on the Verification Officer's queue under the same business
-- registration, where (d)'s index would then reject it with a 409 the operator did not earn.
--
-- No aggregate-id column: MageRide.Shared's PostgresCommandLog omits it when
-- CommandLog:AggregateIdColumn is null, and the fleet a command targets is either in the
-- request path (which the request hash covers) or does not exist yet.
CREATE TABLE IF NOT EXISTS registry.fleet_command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- Reservations that never completed: a request that died between reserving the key and
-- writing its response. The middleware takes them over by age.
CREATE INDEX IF NOT EXISTS ix_fleet_command_log_inflight
  ON registry.fleet_command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE registry.fleet_command_log IS
  'R-14 idempotent replay for fleet-svc (C058). Separate from registry.command_log (0307) on purpose: the two services share a schema but not a key space, and one table would let a key spent against registry-svc replay a fleet-svc command.';
