-- =====================================================================================
-- 0507 — trips: a driver's own session history
-- Source: specs/D3_mageride_api_contracts.md Δ 2026-06-28 (AL-41 driver directory)
--         specs/D5_mageride_business_logic.md BR-28.8 · URD US-24.10
--         backend/contracts/admin-bff.yaml searchDrivers / getDriverDetail
--
-- C064. 0501 indexes `trips.sessions` two ways: `ux_sessions_active_driver`, which is
-- partial on ACTIVE because D-03's rule is "one live session per driver", and
-- `ix_sessions_vehicle`, which is the per-vehicle history. Neither answers "every session
-- this driver has ever run" — the partial one holds at most one row and drops it the moment
-- the session ends, which is exactly the set the directory wants.
--
-- SCR-AP-012's trip count and SCR-AP-013's Trips tab both need it: a Mode A/B driver's
-- journeys are `trips.sessions` rows, not `rides.rides` rows, and a driver directory that
-- counted only Mode C would show a bus driver zero trips.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_sessions_driver
  ON trips.sessions(driver_id, started_at DESC);

COMMENT ON INDEX trips.ix_sessions_driver IS
  'AL-41: a driver''s whole session history (SCR-AP-012/013). ux_sessions_active_driver is partial on ACTIVE and holds at most one row, which is the opposite set.';
