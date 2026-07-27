-- =====================================================================================
-- 1102 — billing: wallet read models
-- Source: server_db_schema.md §10 · ADD §9.1 · D-09
--
-- Mirrors of the ledger, not a second system of record. §10 is explicit: "the double-entry
-- ledger is the master of money; wallets / wallet_transactions are read-model mirrors."
-- They exist so the driver wallet screen (SCR-DA-022) and the dispatch balance check can
-- read one row instead of aggregating postings.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.wallets (
  account_id UUID PRIMARY KEY REFERENCES billing.accounts(id) ON DELETE CASCADE,
  balance_minor BIGINT NOT NULL DEFAULT 0,                    -- signed, mirrors accounts.balance_minor
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('billing','wallets');

COMMENT ON TABLE billing.wallets IS
  'Read-model mirror of billing.accounts.balance_minor (D-09). Rebuildable from the postings at any time — never reconcile the ledger against this.';

CREATE TABLE IF NOT EXISTS billing.wallet_transactions (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  account_id UUID NOT NULL REFERENCES billing.accounts(id) ON DELETE CASCADE,
  entry_id UUID NOT NULL REFERENCES billing.journal_entries(id) ON DELETE CASCADE,
  -- Denormalised copy of billing.journal_entries.kind. Deliberately un-CHECKed here: this
  -- is a projection, and a CHECK that drifts from the entry-side one would reject rows the
  -- ledger has already accepted.
  kind TEXT NOT NULL,
  amount_minor BIGINT NOT NULL,                               -- signed, this account's leg
  balance_after_minor BIGINT NOT NULL,
  description TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_wallet_tx_account
  ON billing.wallet_transactions(account_id, ts DESC);
-- The projection is driven by ledger events, which are at-least-once (C002 decision 3):
-- one row per (account, entry) makes a redelivered event a no-op instead of a duplicate
-- line in the driver's history.
CREATE UNIQUE INDEX IF NOT EXISTS ux_wallet_tx_account_entry
  ON billing.wallet_transactions(account_id, entry_id);

COMMENT ON TABLE billing.wallet_transactions IS
  'Denormalised per-account projection of the journal, for fast wallet history reads. Read model only (§10).';
COMMENT ON INDEX billing.ux_wallet_tx_account_entry IS
  'Dedupe for the at-least-once ledger event stream — a redelivered entry must not append a second history line.';
