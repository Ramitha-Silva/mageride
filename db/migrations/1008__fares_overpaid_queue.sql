-- =====================================================================================
-- 1008 — fares: the R-19 overpaid queue's own index
-- Source: specs/architecture-design-document.md §11.14 (Late Payment Callback & Refund
--         Workflow, R-19/E-05) · §12.4 A08 · build/manifest.yaml C065
--
-- C065. §11.14's late callback moves `fares.ride_payments.state` to `Overpaid` and emits
-- `payment.overpaid` "→ admin queue". 1002 indexes this table three ways — by ride, by the
-- driver-QR claim age, and along the D-10 retry chain — and none of them can answer "everything
-- currently Overpaid, oldest first", which is the whole of the Finance refund queue's second
-- half. Without it every open of SCR-AP-006 scans the payment attempts of the entire platform.
--
-- Partial on the one state, in the same shape as 1003's `ix_refunds_open`: an Overpaid payment
-- is an exception and the set is tiny, so the index holds the queue rather than the table.
-- `created_at` rather than `updated_at` as the key because the queue is worked oldest-first and
-- an operator touching a row must not move it to the back.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_ridepay_overpaid
  ON fares.ride_payments(created_at) WHERE state = 'Overpaid';

COMMENT ON INDEX fares.ix_ridepay_overpaid IS
  'R-19 / ADD §11.14: the admin refund queue''s Overpaid half (SCR-AP-006). A late gateway callback on an already cash-settled ride lands here and waits for a Finance decision.';
