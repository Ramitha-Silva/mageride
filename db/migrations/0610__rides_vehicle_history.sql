-- =====================================================================================
-- 0610 — rides: a vehicle's own ride history
-- Source: specs/D3_mageride_api_contracts.md Δ 2026-06-28 (AL-42 vehicle directory)
--         specs/D5_mageride_business_logic.md BR-28.8 · URD US-24.11
--         backend/contracts/admin-bff.yaml searchVehicles / getVehicleDetail
--
-- C064. 0601 indexes the two sides of a ride somebody asks about by *person* —
-- `ix_rides_driver (accepted_driver_id, created_at DESC)` and `ix_rides_passenger_hist` —
-- because until now every history screen was somebody's own. SCR-AP-014/015 is the first
-- that asks the question by **vehicle**: the directory row carries a trip count and the
-- detail carries the Trips and Earnings tabs, both keyed on `accepted_vehicle_id`, and
-- without this each of them is a sequential scan of every ride the platform has taken.
--
-- Partial on the column being set, which is the same shape 0601's own
-- `ix_rides_retry_of` uses: `accepted_vehicle_id` is NULL for every ride still matching and
-- for every one that expired with no driver, and none of those belongs to a vehicle's
-- history.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_rides_vehicle
  ON rides.rides(accepted_vehicle_id, created_at DESC)
  WHERE accepted_vehicle_id IS NOT NULL;

COMMENT ON INDEX rides.ix_rides_vehicle IS
  'AL-42: the vehicle directory''s trip count and the SCR-AP-015 Trips/Earnings tabs. Partial because a ride with no accepted vehicle belongs to no vehicle''s history.';
