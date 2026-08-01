-- =====================================================================================
-- 1007 — fares: the `wallet` ride-payment method
-- Source: specs/architecture-design-document.md §1.18 (AL-57) · §11.9
--         specs/D5_mageride_business_logic.md §8.1 · backend/contracts/fare.yaml PaymentMethod
--
-- AL-57. **Widened, not narrowed** — see the note at the bottom.
--
-- A card passenger can no longer pay a ride through OnePay: one merchant account per merchant
-- means the fare would land in MageRide's own account with no per-driver sub-account to route it
-- to. Card acceptance moves one step earlier — top up the wallet, where MageRide legitimately IS
-- the payee — and `wallet` is how that balance pays for a ride.
--
-- WHAT MAKES THIS METHOD DIFFERENT FROM EVERY OTHER ONE ON THIS TABLE: it has no acquirer. A
-- `wallet` payment is a single balanced `trip_payment` journal entry — passenger wallet debit,
-- driver wallet credit — inside one transaction, so it reaches `Succeeded` on the spot and there
-- is no `Pending`, no callback and no `provider_transaction_id` to dedupe on. D5' §8.1 carries the
-- transition; nothing in this file enforces it, because the state machine is fare-svc's and a
-- CHECK that restated it would be a second copy to drift.
-- =====================================================================================

ALTER TABLE fares.ride_payments DROP CONSTRAINT IF EXISTS ck_ride_payments_method;
ALTER TABLE fares.ride_payments ADD CONSTRAINT ck_ride_payments_method
  CHECK (method IN ('cash','lankaqr','onepay','cod','scan_driver_qr',
                    -- Δ AL-57: the prepaid balance rail. Money moves passenger wallet -> driver
                    -- wallet on the ledger; MageRide holds it until the AL-58 payout run sweeps it.
                    'wallet'));

COMMENT ON CONSTRAINT ck_ride_payments_method ON fares.ride_payments IS
  'AL-22 + AL-57. Wider than rides.rides.payment_method by design: scan_driver_qr is chosen at settlement, and wallet is the AL-57 card rail. onepay and lankaqr remain admitted only so historical rows stay readable — see the note in this migration.';

-- -------------------------------------------------------------------------------------
-- 'onepay' and 'lankaqr' are NOT removed here, on purpose.
-- -------------------------------------------------------------------------------------
-- AL-57/AL-59 retire both as ride methods, but this CHECK also has to admit every row already
-- written — and fare-svc (C050) still writes them until its own change lands. Narrowing a CHECK
-- under a running writer fails the writer; narrowing it under existing rows fails the migration
-- itself. Both values come out in C050's script, once no code can produce one and the replica's
-- synthetic rows have been re-seeded.
--
-- Which is also why the enum in `backend/contracts/fare.yaml` is already narrow while this CHECK
-- is not: the contract states the intent and wins over the code, and the database is the last
-- thing to move, not the first.
