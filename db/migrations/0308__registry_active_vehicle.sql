-- =====================================================================================
-- 0308 — registry: the driver's currently selected vehicle (the single live publisher)
-- Source: URD US-9.6, US-9.7 · ADD §9.1 D-03 · AL-32 / US-3.6 ("device as single active
--         publisher") · D3' registry-svc route table
--
-- ⚠ Spec gap — micro-change-set, raised in the C021 handoff.
--   US-9.6 is a P0 requirement: "If a driver has registered multiple vehicles, **only one
--   vehicle can go live at a time**", and US-9.7 puts "the registration number of the vehicle
--   currently live/online (**the single active vehicle selected in vehicle management**)" on
--   the driver dashboard. Nothing in D4' §2 or server_db_schema.md §2 stores that selection,
--   and D3' registry-svc has no endpoint that sets it.
--
--   D-03's two enforcement points are both *downstream* of the choice and neither can stand in
--   for it: `ux_sessions_active_driver` (0501) is the Mode A/B tracking plane and
--   `dispatch.driver_presence` (0701) only exists once a driver is already online with a
--   vehicle_id in hand. Something has to answer "which vehicle?" *before* either is written —
--   in the registry, which owns vehicle identity. Hence this column plus
--   POST /v1/vehicles/{id}/select-live.
--
--   **D4' §2 should carry the column and D3' should carry the route.**
--
--   It lands on `registry.driver_profiles` rather than in a table of its own because that row
--   is already 1:1 with the driver — its primary key IS the "only one at a time" half of
--   US-9.6, for free and unbypassable.
-- =====================================================================================

-- Referenced by the composite foreign key below. `id` is already the primary key, so this
-- index is redundant for lookups and exists purely so `(id, owner_id)` can be an FK target:
-- that is what turns "a driver may only select a vehicle they own" from a repository WHERE
-- clause into an invariant the database keeps.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.vehicles'::regclass
                    AND conname = 'ux_vehicles_id_owner') THEN
    ALTER TABLE registry.vehicles ADD CONSTRAINT ux_vehicles_id_owner UNIQUE (id, owner_id);
  END IF;
END $$;

ALTER TABLE registry.driver_profiles
  ADD COLUMN IF NOT EXISTS active_vehicle_id UUID;

ALTER TABLE registry.driver_profiles
  ADD COLUMN IF NOT EXISTS active_vehicle_selected_at TIMESTAMPTZ;

-- MATCH SIMPLE (the default) is deliberate: driver_id is NOT NULL, so the pair is only checked
-- when active_vehicle_id is set, and "no vehicle selected" stays a legal state.
-- ON DELETE SET NULL names its column (PostgreSQL 15+) — without the column list Postgres would
-- try to null driver_id too, which is the primary key.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.driver_profiles'::regclass
                    AND conname = 'fk_driver_profiles_active_vehicle') THEN
    ALTER TABLE registry.driver_profiles
      ADD CONSTRAINT fk_driver_profiles_active_vehicle
      FOREIGN KEY (active_vehicle_id, driver_id)
      REFERENCES registry.vehicles (id, owner_id)
      ON DELETE SET NULL (active_vehicle_id);
  END IF;
END $$;

-- The two columns are set and cleared together, so a dashboard reading US-9.7 never finds a
-- registration number with no instant behind it.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.driver_profiles'::regclass
                    AND conname = 'ck_driver_profiles_active_vehicle_pair') THEN
    ALTER TABLE registry.driver_profiles
      ADD CONSTRAINT ck_driver_profiles_active_vehicle_pair
      CHECK ((active_vehicle_id IS NULL) = (active_vehicle_selected_at IS NULL));
  END IF;
END $$;

COMMENT ON COLUMN registry.driver_profiles.active_vehicle_id IS
  'US-9.6/US-9.7: the one vehicle this driver may go live on, set by POST /v1/vehicles/{id}/select-live. The composite FK to registry.vehicles(id, owner_id) makes ownership a database invariant; APPROVED-ness is not expressible as a constraint and is enforced by registry-svc. C029 must clear the selection when a selected vehicle is DEACTIVATED or REJECTED.';
COMMENT ON CONSTRAINT ux_vehicles_id_owner ON registry.vehicles IS
  'Redundant for lookups (id is the PK). Exists so registry.driver_profiles can reference (id, owner_id) and have Postgres reject a selection of somebody else''s vehicle.';
