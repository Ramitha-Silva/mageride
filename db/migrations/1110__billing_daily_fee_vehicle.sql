-- =====================================================================================
-- 1110 — billing: the daily fee read by vehicle
-- Source: specs/D3_mageride_api_contracts.md Δ 2026-06-28 (AL-42 vehicle directory)
--         specs/D5_mageride_business_logic.md BR-28.8 · URD US-24.11
--         backend/contracts/admin-bff.yaml getVehicleDetail
--
-- C064. `billing.daily_fee_charges` is keyed (driver_id, vehicle_id, fee_date) because
-- D-13's idempotency is per driver per vehicle per Colombo day, and that PK serves every
-- reader it has had so far — all of them start from the driver. SCR-AP-015's Daily-fee tab
-- starts from the **vehicle**, which is a prefix the PK cannot answer, so the tab would
-- scan the whole charge table on every detail open.
--
-- `fee_date DESC` trailing: the tab is the most recent days, and the same index answers
-- "what has this vehicle been charged" newest-first without a sort. No `tz_at` companion is
-- needed in the key — the D-38 audit companion is a column on the row, not an ordering.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_daily_fee_charges_vehicle
  ON billing.daily_fee_charges(vehicle_id, fee_date DESC);

COMMENT ON INDEX billing.ix_daily_fee_charges_vehicle IS
  'AL-42: SCR-AP-015''s Daily-fee tab reads by vehicle, which the (driver_id, vehicle_id, fee_date) PK cannot serve as a prefix.';
