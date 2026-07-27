-- =====================================================================================
-- 1101 — billing: double-entry ledger
-- Source: server_db_schema.md §10 · D4' §10 · ADD §9.1 · D-09, AL-01, AL-03, AL-05
--
-- The MASTER of money on this platform. billing.wallets / wallet_transactions (1102) are
-- read-model mirrors of what is recorded here; a balance disagreement is always resolved
-- in favour of the postings.
--
-- Ordered first in the 11xx range: wallets, voucher purchases, credit transfers and
-- fleet invoices all reference billing.journal_entries.
--
-- AL-01: there is no 'reseller' owner_type and no per-transfer commission — a reselling
--        driver is an ordinary driver account.
-- AL-05: there is no bank-transfer top-up table. Top-up = OnePay card / OnePay wallet /
--        LankaQR / bulk voucher only.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_type TEXT NOT NULL CONSTRAINT ck_accounts_owner_type
    CHECK (owner_type IN ('driver','fleet','platform','suspense')),
  -- iam.users.id for 'driver', registry.fleets.id for 'fleet', NULL for the two singleton
  -- platform accounts — polymorphic, so no FK, and the platform rows have no owner at all.
  owner_id UUID,
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  -- §0: ledger balances are signed BIGINT, unlike every *_minor elsewhere. The suspense
  -- account is negative by construction; driver non-negativity is a service-level rule
  -- (a driver may not go online below the daily fee), not a column CHECK.
  balance_minor BIGINT NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- Neither spec prints it, but the two platform-side accounts are singletons: 'platform'
  -- and 'suspense' carry no owner_id, so without this a second one could be seeded and
  -- postings would silently split across them.
  CONSTRAINT ck_accounts_owner_id CHECK (
    (owner_type IN ('driver','fleet') AND owner_id IS NOT NULL)
    OR (owner_type IN ('platform','suspense') AND owner_id IS NULL)));

CREATE INDEX IF NOT EXISTS ix_accounts_owner ON billing.accounts(owner_type, owner_id);
-- One wallet per owner per currency; and, given the CHECK above, exactly one 'platform'
-- and one 'suspense' account per currency.
CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_owner
  ON billing.accounts(owner_type, owner_id, currency) WHERE owner_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_platform
  ON billing.accounts(owner_type, currency) WHERE owner_id IS NULL;

COMMENT ON TABLE billing.accounts IS
  'Ledger accounts (D-09). No ''reseller'' owner_type — AL-01 makes reselling a behaviour, not a capability. ''fleet'' added by AL-03 for the fleet wallet.';
COMMENT ON COLUMN billing.accounts.balance_minor IS
  'Signed BIGINT per §0 (ledger balances may be negative). Denormalised from the postings for fast reads; the postings remain authoritative.';

CREATE TABLE IF NOT EXISTS billing.journal_entries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  kind TEXT NOT NULL CONSTRAINT ck_journal_entries_kind
    CHECK (kind IN ('topup','daily_fee','trip_payment','penalty_settle','adjustment',
                    'tip_payout','payment_refund','overpaid_reversal','voucher_purchase',
                    'driver_transfer')),
  -- The platform-wide money idempotency key (§0). Callers compose it from the business
  -- fact, never from a random value, so a retry collides here instead of double-posting:
  --   daily fee        'daily_fee:'      || driver_id || ':' || vehicle_id || ':' || fee_date
  --   penalty settle   penalty_id || ':' || ride_id                        (D5 §7.1, exactly)
  --   trip payment     'trip_payment:'   || ride_payment_id
  idempotency_key TEXT NOT NULL UNIQUE,
  description TEXT);

CREATE INDEX IF NOT EXISTS ix_journal_entries_kind ON billing.journal_entries(kind, ts DESC);

COMMENT ON TABLE billing.journal_entries IS
  'One entry per money event (D-09). No ''reseller_commission'' kind: AL-01 removed the per-transfer commission entirely.';
COMMENT ON COLUMN billing.journal_entries.idempotency_key IS
  'D-05 double-apply guard for penalties uses exactly penalty_id || '':'' || rideId (D5 §7.1) — C004 left the real guard here, so that spelling must not drift.';

CREATE TABLE IF NOT EXISTS billing.journal_postings (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  entry_id UUID NOT NULL REFERENCES billing.journal_entries(id) ON DELETE CASCADE,
  account_id UUID NOT NULL REFERENCES billing.accounts(id),
  -- Signed: the debit and the credit of one entry are the same magnitude with opposite
  -- signs, and Σ per entry must be zero (trigger below).
  amount_minor BIGINT NOT NULL);

CREATE INDEX IF NOT EXISTS ix_postings_account ON billing.journal_postings(account_id);
CREATE INDEX IF NOT EXISTS ix_postings_entry ON billing.journal_postings(entry_id);

COMMENT ON TABLE billing.journal_postings IS
  'The legs of each entry. Append-only in practice; Σ amount_minor per entry_id must be 0, enforced by trg_balanced at COMMIT.';

-- -------------------------------------------------------------------------------------
-- Balanced-entry enforcement (D-09).
--
-- DEFERRABLE INITIALLY DEFERRED is what makes this workable: the check runs at COMMIT, so
-- a two-leg entry can be inserted one row at a time inside one transaction without the
-- first insert tripping it. An immediate trigger would reject every entry ever written.
--
-- Extended past the printed DDL to fire on DELETE as well. Both specs print
-- "AFTER INSERT OR UPDATE"; deleting one leg of a balanced entry would then leave the
-- ledger silently unbalanced. Deleting the entry itself still passes — ON DELETE CASCADE
-- removes every leg, and an empty entry sums to zero.
-- -------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION billing.assert_balanced() RETURNS trigger AS $$
DECLARE
  v_entry_id UUID := COALESCE(NEW.entry_id, OLD.entry_id);
BEGIN
  IF (SELECT COALESCE(SUM(amount_minor), 0)
        FROM billing.journal_postings
       WHERE entry_id = v_entry_id) <> 0 THEN
    RAISE EXCEPTION 'journal entry % not balanced', v_entry_id
      USING ERRCODE = 'check_violation';
  END IF;
  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION billing.assert_balanced() IS
  'D-09: rejects a journal entry whose postings do not sum to zero. Deferred to COMMIT so a multi-leg entry can be inserted row by row.';

-- CREATE OR REPLACE TRIGGER is not available for constraint triggers, so drop first —
-- this is what keeps the script re-runnable under migrate-verify.sh pass 3.
DROP TRIGGER IF EXISTS trg_balanced ON billing.journal_postings;
CREATE CONSTRAINT TRIGGER trg_balanced
  AFTER INSERT OR UPDATE OR DELETE ON billing.journal_postings
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION billing.assert_balanced();

-- Platform-side ledger accounts (§20). Seeded here rather than in 1901 because the
-- ux_accounts_platform index above makes them singletons and every later money seed
-- would otherwise have to guess whether they exist yet.
INSERT INTO billing.accounts(owner_type, currency, balance_minor)
  SELECT v.owner_type, 'LKR', 0
    FROM (VALUES ('platform'), ('suspense')) AS v(owner_type)
   WHERE NOT EXISTS (
     SELECT 1 FROM billing.accounts a
      WHERE a.owner_type = v.owner_type AND a.currency = 'LKR' AND a.owner_id IS NULL);
