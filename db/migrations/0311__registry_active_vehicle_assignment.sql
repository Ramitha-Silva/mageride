-- =====================================================================================
-- 0311 — registry: an assigned driver may select a vehicle they do not own
-- Source: URD US-13.9, US-13.8 · AL-03, AL-23 · migration 0308
--
-- 0308 made "a driver may only select a vehicle they own" a database invariant, with a composite
-- foreign key from registry.driver_profiles(active_vehicle_id, driver_id) to
-- registry.vehicles(id, owner_id). That was right for a Mode-C-only walking skeleton and it is
-- **wrong now**: US-13.9 gives an assigned driver — explicitly a non-owner — the right to
-- "select one and go online" with a fleet vehicle, and the composite key rejects exactly that.
--
-- The invariant is not being dropped, it is being **restated**. What must hold is "a driver may
-- only select a vehicle they are *entitled to*", where entitlement is ownership OR a live
-- registry.fleet_assignments row (US-13.8: revoking one takes the right away immediately). That
-- spans two tables and is not expressible as one foreign key, so:
--
--   * the composite FK becomes a plain FK to registry.vehicles(id), which still guarantees the
--     selection names a real vehicle and still nulls it when that vehicle is deleted;
--   * entitlement is enforced by registry-svc against registry.driver_eligible_vehicles (0310),
--     which is the same projection dispatch-svc and trip-state-svc read, so the three cannot
--     disagree about who may operate what;
--   * `ux_vehicles_id_owner` is LEFT IN PLACE. Nothing references it now, but dropping a UNIQUE
--     constraint is not free to put back on a live table, and it costs one index.
--
-- **D4' §2 should carry both halves** — the projection and the fact that the selection is
-- entitlement-scoped rather than owner-scoped. Recorded in the C028 handoff.
-- =====================================================================================

ALTER TABLE registry.driver_profiles
  DROP CONSTRAINT IF EXISTS fk_driver_profiles_active_vehicle;

-- ON DELETE SET NULL still names its column (PostgreSQL 15+): without the column list Postgres
-- would try to null driver_id too, which is the primary key. Same reasoning as 0308.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.driver_profiles'::regclass
                    AND conname = 'fk_driver_profiles_active_vehicle_id') THEN
    ALTER TABLE registry.driver_profiles
      ADD CONSTRAINT fk_driver_profiles_active_vehicle_id
      FOREIGN KEY (active_vehicle_id)
      REFERENCES registry.vehicles (id)
      ON DELETE SET NULL (active_vehicle_id);
  END IF;
END $$;

COMMENT ON COLUMN registry.driver_profiles.active_vehicle_id IS
  'US-9.6/US-9.7/US-13.9: the one vehicle this driver may go live on, set by POST /v1/vehicles/{id}/select-live. One column on a row keyed by driver, so selecting a second releases the first atomically. Entitlement — owned, or a live fleet assignment — is checked by registry-svc against registry.driver_eligible_vehicles; it spans two tables and stopped being expressible as a foreign key when US-13.9 admitted non-owners (0311).';
COMMENT ON CONSTRAINT ux_vehicles_id_owner ON registry.vehicles IS
  'Added by 0308 so driver_profiles could reference (id, owner_id). 0311 dropped that foreign key when US-13.9 made the selection entitlement-scoped rather than owner-scoped; the constraint is kept because a UNIQUE is not free to re-add on a live table.';
