-- =====================================================================================
-- 0608 — rides: who the live offer belongs to, and the price the ride was quoted
-- Source: ADD §11.11 · D5' §6/§6.1 · D3' ride-svc (RideDetail.fare, POST /v1/rides/request
--         202.estimatedFare) · backend/contracts/ride.yaml · R-01, R-02, R-18
--
-- ⚠ Spec gap — micro-change-set, raised in the C022 handoff. D4' §5 / server_db_schema.md §5
--   own `rides.rides`; both need these five columns.
--
--   (a) **The offer has no owner in the ride aggregate.** ADD §11.11 gives `rides.rides` only
--       `current_offer_id` and `offer_expires_at`, so ride-svc knows an offer is live but not
--       which driver holds it. Two contract routes need exactly that fact *before* an accept
--       exists: `GET /v1/rides/{rideId}` and `GET /v1/rides/{rideId}/state` are what the driver
--       app reads to render the offer card and the 15-second countdown, and both answer
--       `403 not-ride-participant` to anyone who is not a party to the ride. Without
--       `offered_driver_id` the only answers available are "let every driver read every offered
--       ride" or "let the offered driver read nothing", and neither is the contract.
--       `dispatch.offers` (0702) holds the same fact, but it belongs to dispatch-svc; ride-svc
--       reaching into it would put two bounded contexts on one table and would still leave
--       ride-svc unable to answer while dispatch is down.
--       `offered_vehicle_id` rides along because ADD §11.11's accept sets
--       `accepted_vehicle_id = :vehicleId`, and the vehicle the offer was made for is the only
--       value that means anything at that moment.
--
--   (b) **The ride cannot remember the price it quoted.** `POST /v1/rides/request` **requires**
--       `estimatedFare` in its 202, `RideDetail.fare` and the `complete` response carry it, and
--       R-18 makes a retry replay the *existing* ride — which is impossible if the amount lived
--       only in the caller's `fareEstimateToken`. `fares.ride_payments` (C005) is settlement,
--       not the quote, and it does not exist until fare-svc runs. The ride aggregate is "what is
--       this ride's commercial state" (ADD Appendix B.2), so the quote belongs on it.
--
--   Deliberately NOT added: `pickup_address` / `dropoff_address`. `Place.address` is optional in
--   the contract and no rule depends on it, so it stays accepted-and-ignored rather than growing
--   the aggregate for presentation text (C022 handoff, contract gap (d)).
-- =====================================================================================

-- The driver dispatch-svc reserved this offer for, and the vehicle they are live on. Both are
-- NULL until the ride reaches Offered and stay set afterwards as the record of who was asked.
ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS offered_driver_id UUID;

ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS offered_vehicle_id UUID;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'fk_rides_offered_driver') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT fk_rides_offered_driver
      FOREIGN KEY (offered_driver_id) REFERENCES iam.users(id);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'fk_rides_offered_vehicle') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT fk_rides_offered_vehicle
      FOREIGN KEY (offered_vehicle_id) REFERENCES registry.vehicles(id);
  END IF;
END $$;

-- The two move together: an offer that names a driver but no vehicle cannot produce an
-- `accepted_vehicle_id`, and one that names a vehicle but no driver has nobody to accept it.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'ck_rides_offer_pair') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT ck_rides_offer_pair
      CHECK ((offered_driver_id IS NULL) = (offered_vehicle_id IS NULL));
  END IF;
END $$;

-- The quote, in integer minor units (§0 Money). NULL only for a row written before this
-- migration; every ride ride-svc books carries one.
ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS fare_estimate_minor BIGINT;

ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS fare_surcharge_minor BIGINT NOT NULL DEFAULT 0;

ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS currency TEXT NOT NULL DEFAULT 'LKR';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'ck_rides_fare_estimate_minor') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT ck_rides_fare_estimate_minor
      CHECK (fare_estimate_minor IS NULL OR fare_estimate_minor >= 0);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'ck_rides_fare_surcharge_minor') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT ck_rides_fare_surcharge_minor
      CHECK (fare_surcharge_minor >= 0);
  END IF;
END $$;

-- LKR only, like every other currency column in the platform (§0).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'ck_rides_currency') THEN
    ALTER TABLE rides.rides ADD CONSTRAINT ck_rides_currency CHECK (currency = 'LKR');
  END IF;
END $$;

-- dispatch-svc's re-offer loop reads "which ride is this driver currently holding an offer on"
-- on every decline and every expiry.
CREATE INDEX IF NOT EXISTS ix_rides_offered_driver
  ON rides.rides(offered_driver_id) WHERE offered_driver_id IS NOT NULL;

COMMENT ON COLUMN rides.rides.offered_driver_id IS
  'The driver the live offer was reserved for (ADD §11.11). Read authorization only: the atomic accept is the conditional UPDATE on (state, current_offer_id, offer_expires_at, version), and gating it on this column instead would make a concurrent double-accept a 403 rather than the 409 §11.11 requires.';
COMMENT ON COLUMN rides.rides.offered_vehicle_id IS
  'The vehicle the offer was made for; becomes accepted_vehicle_id when the offer is won (ADD §11.11).';
COMMENT ON COLUMN rides.rides.fare_estimate_minor IS
  'The upfront quote bound by the fareEstimateToken, in LKR minor units. Required by the POST /v1/rides/request 202 and replayed verbatim on an R-18 retry. The final fare is fare-svc''s (D5'' §1.4).';
