-- =====================================================================================
-- 1409 — registry: the Mode A vehicle's declared route (C062)
-- Source: backend/contracts/admin-bff.yaml #/components/schemas/TrainInput.routeId
--         · D3' admin-bff `POST /v1/admin/trains` · US-2.17/2.18 · AL-09
--
-- **In the 14xx range although it alters a registry table**, because the foreign key
-- points at `spatial.routes`, which 1401 creates. `db/CLAUDE.md`: "A file may create
-- objects in another schema when a foreign key forces the order; name it for what it
-- creates and say why in the header."
--
-- The gap: `TrainInput` carries `routeId` and there is nowhere on the vehicle to put it.
-- `trips.sessions.route_id` (0501) is the route a vehicle is **running right now**, and
-- D4' §4 puts it there deliberately — a bus is reassigned between routes and a column on
-- the vehicle would be wrong for every past journey. A train is the case that argument
-- does not cover: it is registered *for* a line, by an admin, before it has ever run, and
-- US-2.17's registration form asks for it.
--
-- So the two are different questions and both are kept:
--   registry.vehicles.default_route_id  — the line this vehicle is registered for
--   trips.sessions.route_id             — the line this journey actually ran
-- Nothing reads the first to answer the second. Raised as a micro-change-set against
-- server_db_schema.md §2 in the C062 handoff.
-- =====================================================================================

ALTER TABLE registry.vehicles
  ADD COLUMN IF NOT EXISTS default_route_id UUID REFERENCES spatial.routes(id);

-- Partial: only Mode A vehicles ever carry one, and the admin train list is the only
-- reader — "which trains run the coast line" over a table that is mostly Mode C.
CREATE INDEX IF NOT EXISTS ix_vehicles_default_route
  ON registry.vehicles(default_route_id) WHERE default_route_id IS NOT NULL;

COMMENT ON COLUMN registry.vehicles.default_route_id IS
  'The spatial.routes line a Mode A vehicle is REGISTERED for (US-2.17, admin-bff Train.routeId). Not what it is running — that is trips.sessions.route_id, and nothing derives one from the other.';
