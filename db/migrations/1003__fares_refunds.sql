-- =====================================================================================
-- 1003 — fares: refunds and disputes
-- Source: server_db_schema.md §9 · D4' §9 · ADD §9.1 · E-05
--
-- The money movement of a refund is a billing.journal_entries row of kind
-- 'payment_refund' / 'overpaid_reversal'; this table is the gateway-facing workflow
-- around it (requested → submitted → settled), which the ledger has no place for.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS fares.refunds (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_payment_id UUID NOT NULL REFERENCES fares.ride_payments(id),
  kind TEXT NOT NULL CONSTRAINT ck_refunds_kind
    CHECK (kind IN ('full','partial','overpaid_reversal')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  status TEXT NOT NULL DEFAULT 'Requested' CONSTRAINT ck_refunds_status
    CHECK (status IN ('Requested','Submitted','Succeeded','Failed')),
  provider_refund_id TEXT,
  reason_code TEXT,
  -- Bare UUID in both specs: a refund may be requested by a passenger, a Finance Officer
  -- or an automated overpaid-reversal job, and the last of those has no iam.users row.
  requested_by UUID,
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  settled_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_refunds_payment ON fares.refunds(ride_payment_id);
-- The Finance Officer refund queue (SCR-AP-009) is "not yet settled, oldest first".
CREATE INDEX IF NOT EXISTS ix_refunds_open
  ON fares.refunds(requested_at) WHERE status IN ('Requested','Submitted');
-- Gateway reconciliation matches settlements back by the provider's own reference.
CREATE UNIQUE INDEX IF NOT EXISTS ux_refunds_provider_ref
  ON fares.refunds(provider_refund_id) WHERE provider_refund_id IS NOT NULL;

COMMENT ON TABLE fares.refunds IS
  'Refund and dispute workflow (E-05). The ledger effect is a separate billing.journal_entries row; this tracks the gateway round-trip.';
COMMENT ON INDEX fares.ux_refunds_provider_ref IS
  'One refund row per provider reference — the same at-least-once callback problem R-19 solves for payments.';
