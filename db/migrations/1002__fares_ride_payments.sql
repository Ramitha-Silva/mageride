-- =====================================================================================
-- 1002 — fares: ride payment state machine
-- Source: server_db_schema.md §9 + §25 (Δ 2026-07-05 #2) · D4' §9 + Δ 2026-07-05 #2
--         ADD §9.1 · D-10, E-05, E-10, P-04, P-08, R-19, AL-22, AL-47
--
-- Landed in its final (post-Δ) shape. One row per payment ATTEMPT — a retry is a new row
-- pointing back at the one it replaces, which is what makes the D-10 retry chain
-- reconstructable and keeps provider_transaction_id one-to-one with a gateway call.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS fares.ride_payments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id),
  -- The §25 (AL-47) rewrite of this CHECK adds the two driver-QR attestation states but
  -- silently drops 'PartiallyRefunded', which the base §9 DDL, §19's enumeration
  -- reference, ADD §9.1 and fares.refunds.kind='partial' all still require. Landed as the
  -- UNION of both lists — a state that is never written costs nothing, whereas a missing
  -- one makes an E-05 partial refund unrepresentable. Micro-change-set raised.
  state TEXT NOT NULL DEFAULT 'Initiated' CONSTRAINT ck_ride_payments_state
    CHECK (state IN ('Initiated','Pending','Succeeded','Failed','Retried','FellBackToCash',
                     'CashOnDelivery','CashOnDeliveryCollected','Overpaid','Refunded',
                     'PartiallyRefunded','Disputed','QrClaimedByPassenger','DriverConfirmedQR')),
  -- AL-22. Wider than rides.rides.payment_method by design: 'scan_driver_qr' is a
  -- settlement-time choice, 'cod' is a booking-time one (see the C004 handoff, note (f)).
  method TEXT NOT NULL CONSTRAINT ck_ride_payments_method
    CHECK (method IN ('cash','lankaqr','onepay','cod','scan_driver_qr')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  surcharge_minor INTEGER NOT NULL DEFAULT 0 CHECK (surcharge_minor >= 0),   -- OnePay +5% (US-8.11)
  tip_amount_minor INTEGER NOT NULL DEFAULT 0 CHECK (tip_amount_minor >= 0), -- E-10
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  -- P-04: on a proxy booking the booker may pay for a ride they are not taking.
  payer_role TEXT NOT NULL DEFAULT 'rider' CONSTRAINT ck_ride_payments_payer_role
    CHECK (payer_role IN ('rider','booker')),
  payer_user_id UUID REFERENCES iam.users(id),
  retry_of_payment_id UUID REFERENCES fares.ride_payments(id),               -- D-10 retry chain
  -- R-19: the gateway callback is at-least-once and carries no Idempotency-Key of ours,
  -- so this UNIQUE is the dedupe. MageRide.Shared's webhook surface opts out of the
  -- platform idempotency header precisely because this column replaces it (C002).
  provider_transaction_id TEXT UNIQUE,
  attempt_no SMALLINT NOT NULL DEFAULT 1 CHECK (attempt_no >= 1),
  -- AL-47 driver-QR attestation. Bank-to-bank: no gateway callback ever arrives, so the
  -- two taps below are the only settlement evidence there is.
  qr_claimed_at TIMESTAMPTZ,                                  -- passenger "I have paid"
  qr_confirmed_at TIMESTAMPTZ,                                -- driver "QR payment received"
  qr_claim_artifact_id UUID REFERENCES rides.proof_artifacts(id),  -- optional receipt screenshot
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('fares','ride_payments');

CREATE INDEX IF NOT EXISTS ix_ridepay_ride ON fares.ride_payments(ride_id);
-- The driver-QR attestation queue: claimed by the passenger, not yet confirmed by the
-- driver. D5 escalates these on a timer, so the scan is by age (AL-47).
CREATE INDEX IF NOT EXISTS ix_ridepay_qr_unconfirmed
  ON fares.ride_payments(qr_claimed_at) WHERE state = 'QrClaimedByPassenger';
-- Finance reconciliation and the refund queue both walk the retry chain backwards.
CREATE INDEX IF NOT EXISTS ix_ridepay_retry_of
  ON fares.ride_payments(retry_of_payment_id) WHERE retry_of_payment_id IS NOT NULL;

COMMENT ON TABLE fares.ride_payments IS
  'Mode C payment state machine (D-10, P-08). One row per attempt; retries chain through retry_of_payment_id rather than mutating the original.';
COMMENT ON COLUMN fares.ride_payments.provider_transaction_id IS
  'R-19 callback idempotency. UNIQUE is the whole mechanism: a replayed OnePay/LankaQR callback collides here instead of double-crediting.';
COMMENT ON COLUMN fares.ride_payments.qr_confirmed_at IS
  'AL-47: the driver earning posts on DriverConfirmedQR exactly as it does on CashSettled (R-05).';
COMMENT ON COLUMN fares.ride_payments.state IS
  'Union of the §9 base list and the §25 (AL-47) rewrite. PartiallyRefunded survives from the base list because fares.refunds.kind still admits ''partial''.';
