-- =====================================================================================
-- Walking-skeleton seed — one approved Mode C three-wheeler for one driver (C021)
--
-- NOT a migration. This file lives outside db/migrations/ on purpose: DbUp applies every
-- script in that directory to every database including production, and this one invents a
-- driver account and waves a vehicle past AL-10's insurance requirement. It is applied by
-- `bash infra/scripts/seed-skeleton.sh` and by Registry.Api.Tests, never by the migrate
-- container.
--
-- What it creates (C021 deliverable "seed script creating one approved Mode C three-wheeler
-- for the skeleton driver"):
--   iam.users                  the driver, role=driver, phone +94770000001
--   iam.user_roles             the driver grant — opening the Driver App does not confer it
--                              (C020 decision 4), so the seed has to make it explicit
--   registry.driver_profiles   the profile row, with the vehicle selected as live (US-9.6)
--   registry.vehicles          one APPROVED Mode C three_wheeler, plate WP-QA-0001
--
-- Identifiers are fixed so C022-C025 can name the same driver and vehicle without reading
-- them back out of the database:
--   driver  00000000-0000-4000-8000-00000000d001
--   vehicle 00000000-0000-4000-8000-00000000c001
--
-- Re-runnable. Every statement is ON CONFLICT / conditional, and re-running never reverts a
-- change made through the API — a plate renamed by hand stays renamed, because the conflict
-- targets are the primary keys, not the values.
-- =====================================================================================

BEGIN;

INSERT INTO iam.users (id, phone, role, first_name, language)
VALUES ('00000000-0000-4000-8000-00000000d001', '+94770000001', 'driver', 'Skeleton', 'en')
ON CONFLICT (id) DO NOTHING;

INSERT INTO iam.user_roles (user_id, role)
VALUES ('00000000-0000-4000-8000-00000000d001', 'driver')
ON CONFLICT (user_id, role) DO NOTHING;

INSERT INTO registry.driver_profiles (driver_id, display_name)
VALUES ('00000000-0000-4000-8000-00000000d001', 'Skeleton Driver')
ON CONFLICT (driver_id) DO NOTHING;

-- AL-09 canonical type; three_wheeler is the launch vehicle for Mode C. status=APPROVED is
-- the dev seed path this component's scope allows: no document was uploaded and no OCR ran,
-- so AL-10 and the AL-30 step machine are bypassed. C029 owns the real gate.
INSERT INTO registry.vehicles
  (id, owner_id, registration_number, vehicle_type, mode, status, onboarding_status, driver_name)
VALUES
  ('00000000-0000-4000-8000-00000000c001',
   '00000000-0000-4000-8000-00000000d001',
   'WP-QA-0001', 'three_wheeler', 'C', 'APPROVED', 'approved', 'Skeleton Driver')
ON CONFLICT (id) DO NOTHING;

-- US-9.6: the single live publisher. Set here rather than left to a POST /select-live call so
-- the seed alone satisfies "a driver has exactly one selectable approved Mode C vehicle after
-- seeding" — the API path is exercised by Registry.Api.Tests.
UPDATE registry.driver_profiles
   SET active_vehicle_id = '00000000-0000-4000-8000-00000000c001',
       active_vehicle_selected_at = now()
 WHERE driver_id = '00000000-0000-4000-8000-00000000d001'
   AND active_vehicle_id IS DISTINCT FROM '00000000-0000-4000-8000-00000000c001';

COMMIT;

-- Fails the script rather than reporting success on a half-seeded database.
DO $$
DECLARE
  selectable INTEGER;
BEGIN
  SELECT count(*) INTO selectable
    FROM registry.vehicles v
    JOIN registry.driver_profiles p ON p.active_vehicle_id = v.id
   WHERE v.owner_id = '00000000-0000-4000-8000-00000000d001'
     AND v.status = 'APPROVED'
     AND v.mode = 'C';

  IF selectable <> 1 THEN
    RAISE EXCEPTION
      'skeleton seed: expected exactly one selected, approved Mode C vehicle for the skeleton driver, found %',
      selectable;
  END IF;
END $$;
