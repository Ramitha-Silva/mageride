-- =====================================================================================
-- 0713 — dispatch: index the offer log for the D-13 daily-fee trip count
-- Source: D5' §2.2 · ADD §9.1 D-08/D-13 · migrations 0702, 0712
--
-- ⚠ Missing index — micro-change-set, raised in the C047 handoff.
--   D5' §2.2's `tripsToday` is "count(completed+accepted today for driver)", and this platform
--   answers it from `dispatch.offers`: an ACCEPTED offer is the service-of-record's own note that
--   the driver took a trip (C034's DailyFeeRepository makes the argument). Two callers ask it on
--   the hot path — dispatch-svc's D-08 pre-dispatch gate, per candidate per round, and
--   subscription-svc's charge, once per accept — and 0702 indexes this table only by `ride_id`
--   and by the partial-unique live-offer predicate. Neither serves
--   "this driver's ACCEPTED offers within a Colombo day", so both were sequential scans that
--   grow with the whole platform's offer history.
--
--   Partial on `status = 'ACCEPTED'` because that is the only status either caller counts, and
--   because DECLINED and EXPIRED are the bulk of the table — a cascade of fifteen-second offers
--   leaves one accept behind. `responded_at` leads the ordering after `driver_id` so the day
--   window is a range scan; it is NULL until the driver answers, and a NULL never falls inside a
--   day range, so an unanswered offer is excluded by the predicate rather than by an index hint.
--
--   **D4' §6 / server_db_schema.md §6 should carry this index**, or §6 should say which table
--   answers `tripsToday`.
--
-- Adds no table and no column: `migrate-verify.sh`'s "13 dispatch tables" check is unchanged.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_offers_driver_responded
  ON dispatch.offers(driver_id, responded_at DESC)
  WHERE status = 'ACCEPTED';

COMMENT ON INDEX dispatch.ix_offers_driver_responded IS
  'D5'' §2.2 tripsToday: a driver''s accepted offers within an Asia/Colombo day. Read by dispatch-svc''s D-08 gate (per candidate) and by subscription-svc''s D-13 charge (per accept) — the two must count the same rows or the gate mispredicts the charge it exists to anticipate.';
