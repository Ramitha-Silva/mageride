-- =====================================================================================
-- 1006 — fares: one first attempt per ride
-- Source: server_db_schema.md §9 · D4' §9 · D-10, R-14
--
-- ⚠ Spec gap — micro-change-set, raised in the C049 handoff.
--
--   `POST /v1/fare/calculate` is called by ride-svc on completion and its delivery is
--   at-least-once. What must never happen twice is a *ride being priced* — two fares for one
--   journey is two amounts a passenger could be asked for — and §9 declares no index that says
--   so. It could not declare a plain UNIQUE on ride_id either, because D-10's retry chain
--   deliberately puts several payment ATTEMPTS on one ride (1002's own header says so).
--
--   The invariant is therefore not "one payment per ride" but "one FIRST attempt per ride",
--   which is exactly expressible: attempt_no starts at 1 and a retry increments it.
--
--   An application-side guard is not enough and was tried first: a `SELECT … FOR UPDATE` that
--   matches no row locks nothing, so six concurrent completions all see an empty result and all
--   insert. Caught by `Concurrent_completions_leave_one_payment`.
--
--   **D4' §9 / server_db_schema.md §9 should carry this index.**
-- =====================================================================================

CREATE UNIQUE INDEX IF NOT EXISTS ux_ride_payments_first_attempt
  ON fares.ride_payments(ride_id) WHERE attempt_no = 1;

COMMENT ON INDEX fares.ux_ride_payments_first_attempt IS
  'One first payment attempt per ride: a retried POST /v1/fare/calculate collides here instead of pricing the journey twice. Partial on attempt_no = 1 because D-10''s retry chain is several attempts on one ride (1002).';
