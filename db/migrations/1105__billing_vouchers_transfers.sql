-- =====================================================================================
-- 1105 — billing: bulk vouchers and driver-to-driver credit transfers
-- Source: server_db_schema.md §10 · D4' §10 · ADD §9.1/§432 · D-09, AL-01, US-9.13/9.19/9.21
--
-- AL-01, in three parts, all of which this file encodes:
--   1. There is no reseller role, account or capability. A "reseller" is any driver who
--      bought bulk credit cheaply and resells it at face value.
--   2. The margin is the PURCHASE discount, configured per voucher denomination and
--      applied only at purchase (billing.voucher_discount_tiers).
--   3. A driver-to-driver transfer moves the EXACT value — no commission posting, and no
--      'reseller_commission' journal kind exists to record one.
--
-- Voucher tier seed rows are in 1901.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS billing.voucher_discount_tiers (
  -- The voucher's face value is the key: the discount is per denomination, not per driver
  -- and not per transfer (AL-01). e.g. 100000 = Rs 1,000.
  denomination_minor BIGINT PRIMARY KEY CHECK (denomination_minor > 0),
  -- Basis points, so a 10% rate is 1000: pay 90,000, receive 100,000 of credit.
  discount_bps INTEGER NOT NULL CHECK (discount_bps BETWEEN 0 AND 10000),
  active BOOLEAN NOT NULL DEFAULT true,
  updated_by UUID REFERENCES iam.users(id),                   -- the admin who set the tier
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('billing','voucher_discount_tiers');

COMMENT ON TABLE billing.voucher_discount_tiers IS
  'Bulk-voucher purchase discount per voucher VALUE, admin-set in Admin Portal Config (SCR-AP-007, AL-01). This percentage is the informal reseller''s entire margin — there is no per-transfer commission anywhere in the platform.';

CREATE TABLE IF NOT EXISTS billing.voucher_purchases (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  buyer_id UUID NOT NULL REFERENCES iam.users(id),
  denomination_minor BIGINT NOT NULL CHECK (denomination_minor >= 0),
  discount_bps_applied INTEGER NOT NULL CHECK (discount_bps_applied BETWEEN 0 AND 10000),
  paid_minor BIGINT NOT NULL CHECK (paid_minor >= 0),         -- charged to the buyer
  credited_minor BIGINT NOT NULL CHECK (credited_minor >= 0), -- credited to the buyer's wallet
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  gateway_ref TEXT,
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- US-9.19: the voucher credits its full face value; the discount only reduces what was
  -- paid. Recorded as a constraint because the two columns are otherwise free to drift and
  -- a wrong credited_minor is a direct loss.
  CONSTRAINT ck_voucher_purchases_credited CHECK (credited_minor = denomination_minor),
  CONSTRAINT ck_voucher_purchases_paid CHECK (paid_minor <= denomination_minor));

CREATE INDEX IF NOT EXISTS ix_voucher_purchases_buyer
  ON billing.voucher_purchases(buyer_id, created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_voucher_purchases_gateway_ref
  ON billing.voucher_purchases(gateway_ref) WHERE gateway_ref IS NOT NULL;

COMMENT ON TABLE billing.voucher_purchases IS
  'Bulk credit voucher purchase (US-9.19). Credits the BUYER''S OWN wallet immediately — there is no redeem code and no second party.';
COMMENT ON COLUMN billing.voucher_purchases.credited_minor IS
  'Always equals denomination_minor. The discount lives entirely in paid_minor: pay Rs 900, receive Rs 1,000 of credit.';

CREATE TABLE IF NOT EXISTS billing.credit_transfers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  sender_driver_id UUID NOT NULL REFERENCES iam.users(id),    -- credit holder / approver
  recipient_driver_id UUID NOT NULL REFERENCES iam.users(id), -- requester / recipient
  -- Debited from the sender and credited to the recipient unchanged. No commission leg.
  amount_minor BIGINT NOT NULL CHECK (amount_minor > 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  direction TEXT NOT NULL DEFAULT 'REQUESTED' CONSTRAINT ck_credit_transfers_direction
    CHECK (direction IN ('REQUESTED','DIRECT')),
  status TEXT NOT NULL DEFAULT 'PENDING' CONSTRAINT ck_credit_transfers_status
    CHECK (status IN ('PENDING','APPROVED','REJECTED')),
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- Transferring to yourself would post a debit and a credit to the same account: it sums
  -- to zero, satisfies trg_balanced and moves nothing, but pollutes both drivers' history.
  CONSTRAINT ck_credit_transfers_not_self
    CHECK (sender_driver_id <> recipient_driver_id),
  -- Only an APPROVED transfer moves money, so only it may carry a ledger entry.
  CONSTRAINT ck_credit_transfers_posting
    CHECK (status = 'APPROVED' OR journal_entry_id IS NULL));

CREATE INDEX IF NOT EXISTS ix_credit_transfers_sender
  ON billing.credit_transfers(sender_driver_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_credit_transfers_recipient
  ON billing.credit_transfers(recipient_driver_id, created_at DESC);
-- The sender's approval inbox (US-9.13): requests waiting on them.
CREATE INDEX IF NOT EXISTS ix_credit_transfers_pending
  ON billing.credit_transfers(sender_driver_id, created_at DESC) WHERE status = 'PENDING';

COMMENT ON TABLE billing.credit_transfers IS
  'Driver-to-driver credit transfer by Driver ID (US-9.13/9.21). EXACT value, NO commission (AL-01): the ledger entry is a two-leg driver_transfer with equal and opposite amounts.';

-- billing.bank_transfer_topups is deliberately absent (AL-05). Top-up = OnePay card /
-- OnePay wallet / LankaQR / bulk voucher. Do not add it back without a spec change.
