-- =====================================================================================
-- 0712 — dispatch: an accepted offer stops being "live" when its ride ends
-- Source: server_db_schema.md §6 · D5' §3.6 · ADD §11.11 "Why both" · R-10
--
-- ⚠ Bug fix + spec gap — micro-change-set, raised in the C034 handoff.
--   R-10 is "Single live offer per driver guaranteed", and §6 spells it
--       CREATE UNIQUE INDEX ux_offers_driver_live ON dispatch.offers(driver_id)
--         WHERE status IN ('OFFERED','ACCEPTED');
--   with `status` CHECKed to ('OFFERED','ACCEPTED','DECLINED','EXPIRED'). Nothing in either list
--   is a *terminal* state for an accepted offer — a completed ride leaves its row at ACCEPTED for
--   ever — so as printed the index does not say "one live offer per driver", it says **one accepted
--   offer per driver per lifetime**: the second ride a driver is ever offered is refused by a
--   unique violation against the first one they finished, and every ride after that too.
--
--   The two ways to spell the fix inside the printed DDL both lose something real: settling the row
--   to DECLINED or EXPIRED makes the audit lie about what the driver did, and dropping ACCEPTED
--   from the index predicate would let a driver hold an offer for a second ride while carrying a
--   passenger on the first — which is the exact race R-10 exists to prevent.
--
--   So the *liveness* gains the dimension it was missing, and neither printed list changes: the
--   status still records what happened, and `released_at` records when the offer stopped being
--   something the driver is on the hook for. dispatch-svc stamps it when the ride reaches a
--   terminal state and the driver returns to the candidate pool (ADD §11.12's "Driver availability
--   after terminal cancellation").
--
--   **server_db_schema.md §6 / D4' §6 should carry `released_at` and the widened index predicate**,
--   or §6 should add a terminal offer status and say which move writes it.
--
-- Adds no table: `migrate-verify.sh`'s "13 dispatch tables" check is unchanged.
-- =====================================================================================

ALTER TABLE dispatch.offers ADD COLUMN IF NOT EXISTS released_at TIMESTAMPTZ;

-- Backfill before the index is rebuilt, or an existing pair of finished rides for one driver would
-- make the CREATE UNIQUE INDEX itself fail. An ACCEPTED row whose ride is already terminal is by
-- definition released; `rides.rides.terminal_at` is ride-svc's own record of when (0601, written
-- since C032), so the timestamp is the truth rather than "whenever this migration ran".
UPDATE dispatch.offers o
   SET released_at = COALESCE(r.terminal_at, now())
  FROM rides.rides r
 WHERE r.id = o.ride_id
   AND o.status = 'ACCEPTED'
   AND o.released_at IS NULL
   AND r.terminal_at IS NOT NULL;

DROP INDEX IF EXISTS dispatch.ux_offers_driver_live;

CREATE UNIQUE INDEX IF NOT EXISTS ux_offers_driver_live
  ON dispatch.offers(driver_id)
  WHERE status IN ('OFFERED','ACCEPTED') AND released_at IS NULL;

COMMENT ON COLUMN dispatch.offers.released_at IS
  'When this offer stopped counting against R-10''s one-live-offer-per-driver rule. NULL while the driver is on the hook for it; stamped by dispatch-svc when the ride reaches a terminal state. See the 0712 header — without it an ACCEPTED row is live for ever and a driver can only be offered one ride in their lifetime.';
COMMENT ON INDEX dispatch.ux_offers_driver_live IS
  'R-10: one live offer per driver. Widened by 0712 with `released_at IS NULL` so a finished ride stops blocking the next one.';
