-- =====================================================================================
-- 1109 — billing: the passenger wallet, and the weekly driver payout run
-- Source: specs/architecture-design-document.md §1.18 (AL-57, AL-58) · §11.9 · §11.9a
--         specs/D3_mageride_api_contracts.md payout-svc · backend/contracts/payout.yaml
--         specs/server_db_schema.md §10 · D4' §10 · D-09, D-38, R-19
--
-- AL-57/AL-58. Four changes: an account type, a journal kind, and the two payout tables.
--
-- ⚠ THIS MIGRATION IS WHERE MageRide STARTS HOLDING OTHER PEOPLE'S MONEY. Everything before it
-- was pass-through: a driver's wallet held prepaid credit the driver had paid IN, and AL-49 kept
-- subscriber money moving straight to the fleet owner. A passenger balance is a customer's funds
-- on the platform's books and a driver's balance is now a liability, so the two halves land
-- together — the account type that receives the money and the tables that get it out again.
-- The licensing question that raises (CBSL authorisation, a sponsor bank for LankaPay/CEFTS) is
-- recorded in ADD §1.18 and is a go-live gate, not something a migration settles.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (1) `passenger` accounts (AL-57)
-- -------------------------------------------------------------------------------------
-- §10's owner_type carried driver / fleet / platform / suspense: the "wallet" this platform had
-- built was the DRIVER's, for daily fees. A passenger wallet had no ledger existence at all, so
-- this is a new account type rather than a reuse. DROP-then-ADD because ALTER TABLE has no
-- ADD CONSTRAINT IF NOT EXISTS — 1108's pattern.
ALTER TABLE billing.accounts DROP CONSTRAINT IF EXISTS ck_accounts_owner_type;
ALTER TABLE billing.accounts ADD CONSTRAINT ck_accounts_owner_type
  CHECK (owner_type IN ('passenger','driver','fleet','platform','suspense'));

-- A passenger owns their account exactly as a driver does; only the two singleton platform-side
-- accounts have no owner. Restated in full because the original is one expression.
ALTER TABLE billing.accounts DROP CONSTRAINT IF EXISTS ck_accounts_owner_id;
ALTER TABLE billing.accounts ADD CONSTRAINT ck_accounts_owner_id CHECK (
  (owner_type IN ('passenger','driver','fleet') AND owner_id IS NOT NULL)
  OR (owner_type IN ('platform','suspense') AND owner_id IS NULL));

COMMENT ON CONSTRAINT ck_accounts_owner_type ON billing.accounts IS
  'AL-01 removed ''reseller''; AL-03 added ''fleet''; AL-57 added ''passenger'' — a card top-up is held as a prepaid balance and a ride is paid from it, because OnePay has one merchant account per merchant and a card fare could not otherwise reach the driver.';

-- -------------------------------------------------------------------------------------
-- (2) The `driver_payout` journal kind (AL-58)
-- -------------------------------------------------------------------------------------
-- The entry that discharges what an AL-57 wallet fare created: driver wallet debit against the
-- platform account. Keyed 'driver_payout:' || payout_id, so a retried run collides on
-- journal_entries.idempotency_key instead of paying twice.
ALTER TABLE billing.journal_entries DROP CONSTRAINT IF EXISTS ck_journal_entries_kind;
ALTER TABLE billing.journal_entries ADD CONSTRAINT ck_journal_entries_kind
  CHECK (kind IN ('topup','daily_fee','trip_payment','penalty_settle','adjustment',
                  'tip_payout','payment_refund','overpaid_reversal','voucher_purchase',
                  'driver_transfer','fleet_invoice',
                  -- Δ AL-58. Still no 'reseller_commission' — AL-01 removed it and neither this
                  -- file nor 1108 puts it back.
                  'driver_payout'));

-- -------------------------------------------------------------------------------------
-- (3) The weekly sweep (AL-58)
-- -------------------------------------------------------------------------------------
-- One row per run. `run_date` is the Asia/Colombo business date and is UNIQUE, which is what makes
-- "run the sweep twice for one date" a 409 rather than a second set of instructions — the policy is
-- a FULL sweep with no minimum, so a second run in a day would raise a zero-value instruction for
-- every driver it had just emptied. `tz_at` is D-38's mandatory instant companion to a business
-- DATE; migrate-verify enforces the pairing.
CREATE TABLE IF NOT EXISTS billing.payout_batches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_date DATE NOT NULL UNIQUE,
  tz_at TIMESTAMPTZ NOT NULL,
  status TEXT NOT NULL DEFAULT 'RUNNING' CONSTRAINT ck_payout_batches_status
    CHECK (status IN ('RUNNING','COMPLETED','FAILED')),
  instruction_count INT NOT NULL DEFAULT 0 CHECK (instruction_count >= 0),
  total_minor BIGINT NOT NULL DEFAULT 0 CHECK (total_minor >= 0),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ);

COMMENT ON TABLE billing.payout_batches IS
  'One weekly payout run (AL-58). run_date is UNIQUE: the sweep is idempotent on the Colombo business date, not on a header, because a second run the same day would empty a wallet that is already empty.';

-- One instruction per driver per batch. The wallet debit (`journal_entry_id`) and this row commit
-- TOGETHER — an instruction with no debit pays a driver twice on a retry, and a debit with no
-- instruction loses their money. The column is NOT NULL for exactly that reason: the schema
-- refuses to hold half of the pair.
CREATE TABLE IF NOT EXISTS billing.payouts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  batch_id UUID NOT NULL REFERENCES billing.payout_batches(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  payout_profile_id UUID NOT NULL REFERENCES registry.driver_payout_profiles(id),
  -- The driver's WHOLE balance at sweep time: weekly full sweep, no minimum, no holdback
  -- (Payout:RetainMinor = 0). Non-negative like every other *_minor column; a zero-value
  -- instruction is refused by ck_payouts_positive rather than by the >= 0 bound, so the two
  -- failures read differently in a log.
  amount_minor BIGINT NOT NULL CHECK (amount_minor >= 0),
  CONSTRAINT ck_payouts_positive CHECK (amount_minor > 0),
  status TEXT NOT NULL DEFAULT 'PENDING' CONSTRAINT ck_payouts_status
    CHECK (status IN ('PENDING','SUBMITTED','PAID','FAILED')),
  failure_reason TEXT,
  provider_reference TEXT,
  journal_entry_id UUID NOT NULL REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- A FAILED instruction has already had its debit reversed, so it must say why; anything else
  -- must not carry a reason it did not earn.
  CONSTRAINT ck_payouts_failure_reason
    CHECK ((status = 'FAILED') = (failure_reason IS NOT NULL)));

-- One instruction per driver per run. Without it a retried sweep that got halfway would pay the
-- drivers it had already reached a second time.
CREATE UNIQUE INDEX IF NOT EXISTS ux_payouts_batch_driver
  ON billing.payouts(batch_id, driver_id);
-- R-19's shape: the bank's own reference is unique where present, so a redelivered result neither
-- pays twice nor reverses twice.
CREATE UNIQUE INDEX IF NOT EXISTS ux_payouts_provider_ref
  ON billing.payouts(provider_reference) WHERE provider_reference IS NOT NULL;
-- The two reads that matter: a driver's own history (SCR-DA-022a) and Finance's exception queue.
CREATE INDEX IF NOT EXISTS ix_payouts_driver ON billing.payouts(driver_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_payouts_open
  ON billing.payouts(created_at) WHERE status IN ('PENDING','SUBMITTED','FAILED');

SELECT public.attach_set_updated_at('billing','payouts');

COMMENT ON TABLE billing.payouts IS
  'One payout instruction per driver per weekly batch (AL-58). journal_entry_id is NOT NULL because the wallet debit and this row commit together — the schema refuses to hold half the pair. FAILED reverses the debit under the same idempotency-key discipline, exactly once, and the next run sweeps the restored balance.';
COMMENT ON COLUMN billing.payouts.provider_reference IS
  'The bank origination adapter''s own id, once submitted. UNIQUE where present (R-19) — a redelivered result is a no-op rather than a second payment or a second reversal.';
COMMENT ON INDEX billing.ix_payouts_open IS
  'Finance''s exception queue (SCR-AP-006): everything not yet PAID, including the FAILED rows whose money is back on the driver''s wallet.';
