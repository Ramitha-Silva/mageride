-- =====================================================================================
-- 1316 — registry.driver_profiles.allowed_vehicle_types holds AL-09 types, not licence
--        classes (Δ MCS-13, repairing data written by the MCS-11 defect).
--
-- WHAT WENT WRONG. `registry.yaml` types `allowedVehicleTypes` as `VehicleType[]` on the
-- request and on the 200 of `PUT /v1/drivers/profile`, and `GET /v1/drivers/profile`
-- answers the same shape. registry-svc validated that on the value a DRIVER typed
-- (`RequireAllowedVehicleTypes`) and NOT on the value extraction returned, which went to
-- this column through `SplitTypes` unchecked.
--
-- Harmless for exactly as long as extraction was broken. Once MCS-07 fixed it, the first
-- real read of a Sri Lankan licence came back `B,G1` at confidence 1.00 — the licence
-- CLASSES, which is what `GeminiPrompts` asks for and is correct, because what is printed
-- on the card is the evidence a Verification Officer checks. They are not AL-09 vehicle
-- types. This column then held them, the API answered them inside an enum-typed field, and
-- every strict client rejected the whole body: "Something went wrong. Please try again."
-- on SCR-DA-003a, after a 200.
--
-- MCS-11 stopped new ones being written. It could not repair the rows already there, and
-- those rows are STICKY: `DriverProfileRepository`'s upsert is
--
--     allowed_vehicle_types = COALESCE(EXCLUDED.allowed_vehicle_types, <existing>)
--
-- which is deliberate — a vehicle registration must not wipe the profile's types — and it
-- means a driver whose row holds `{B,G1}` keeps it through every later save, because the
-- fix supplies NULL for a value with nothing canonical in it. Without this migration that
-- driver is stuck behind the same error for ever, on a build that has already fixed it.
--
-- WHAT THIS DOES. Filters every row to its canonical members and NULLs the ones that have
-- none. `{B,G1}` becomes NULL; a mixed `{B,sedan}` keeps `{sedan}`; an already-clean row is
-- untouched. NULL is right rather than `{}`: "nobody has established which types this
-- driver may operate" is exactly the state a new profile is in, it is what sends the field
-- to the officer queue, and `{}` would assert the driver is allowed nothing.
--
-- Idempotent, and safe to run against a database that never had a bad row.
--
-- NO CHECK CONSTRAINT, deliberately. It would be the strongest guarantee and it is a
-- bigger decision than this repair: the column's own comment in 0304 says "licence classes
-- (US-2.4a)", which is the opposite of what the contract says, and settling that
-- contradiction — and auditing every writer, fleet-svc included — belongs in the
-- micro-change-set that also has to decide what a class B licence entitles a driver to.
-- Raised in the MCS-11 handoff.
-- =====================================================================================

-- The AL-09 set, identical to `VehicleTypes.All` and to `registry.vehicles.vehicle_type`'s
-- CHECK in 0303. A rename in one of the three has to be a rename in all three.
UPDATE registry.driver_profiles p
   SET allowed_vehicle_types = NULLIF(
         ARRAY(
           SELECT t
             FROM unnest(p.allowed_vehicle_types) AS t
            WHERE t IN ('motorbike', 'three_wheeler', 'flex', 'sedan', 'mini_van',
                        'van', 'truck', 'mini_truck', 'bus', 'train')
         ),
         '{}'
       )
 WHERE p.allowed_vehicle_types IS NOT NULL
   AND EXISTS (
         SELECT 1
           FROM unnest(p.allowed_vehicle_types) AS t
          WHERE t NOT IN ('motorbike', 'three_wheeler', 'flex', 'sedan', 'mini_van',
                          'van', 'truck', 'mini_truck', 'bus', 'train')
       );

-- 0304 says "licence classes (US-2.4a)", which is what led here: the column was documented
-- as holding one vocabulary and answered by the API as another. The contract wins.
COMMENT ON COLUMN registry.driver_profiles.allowed_vehicle_types IS
  'AL-09 canonical vehicle types (VehicleTypes.All), NOT licence classes. The classes a '
  'licence prints are kept as the extracted evidence on registry.document_fields; this '
  'column is answered as VehicleType[] by registry.yaml and a non-canonical value here '
  'fails every strict client (Δ MCS-13).';
