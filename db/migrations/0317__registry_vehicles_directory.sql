-- =====================================================================================
-- 0317 — registry: the keyset index the vehicle directory pages on
-- Source: specs/D3_mageride_api_contracts.md Δ 2026-06-28 (AL-42 vehicle directory)
--         specs/D5_mageride_business_logic.md BR-28.8 · URD US-24.11
--         backend/contracts/admin-bff.yaml searchVehicles
--
-- C064. `GET /v1/admin/vehicles` pages over `registry.vehicles` by
-- (created_at DESC, id DESC) — registration order, newest first, which is what SCR-AP-014
-- lists and what the opaque cursor encodes. 0303's three indexes are all about *finding* a
-- vehicle (plate, owner, mode+status); none of them orders by registration date, so
-- without this the first page of a large registry is a full sort.
--
-- Not partial on `status`: the directory's status filter is one of five values including
-- "any", and D-37 keeps DEACTIVATED rows for ever — the screen that looks up a retired
-- registration is exactly the one an operator opens when a plate has been reissued.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_vehicles_created
  ON registry.vehicles(created_at DESC, id DESC);

COMMENT ON INDEX registry.ix_vehicles_created IS
  'AL-42: the vehicle directory keyset (created_at/id cursor). Ordering key only — reg-no/type/mode/owner/fleet filters are applied over the page.';
