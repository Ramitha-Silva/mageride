-- =====================================================================================
-- 0609 — rides: the package recipient, and the location-request expiry scan
-- Source: ADD §11.15/§11.16 · D5' §10/§11 · D3' ride-svc `POST /v1/rides/request`
--         · backend/contracts/ride.yaml · P-02, P-03, P-07, P-10, AL-21, AL-33, AL-45
--
-- ⚠ Spec gap — micro-change-set, raised in the C037 handoff. D4' §5 / server_db_schema.md §5
--   own `rides.rides`; both need the two recipient columns.
--
--   (a) **A package has a recipient and the aggregate has nowhere to put one.** D3'
--       `RideRequest` carries `recipientName` and `recipientPhone` for a `kind=package`
--       booking, AL-21 makes the recipient the subject of a notification on pickup-confirm
--       ("registered recipient → FCM deep-link; unregistered recipient → SMS with a
--       `safety.trip_share_tokens` web link"), and AL-33 puts a **call button** for the
--       recipient on the driver's delivery sheet (SCR-DA/DI-016b/c). `rides.rides` prints
--       `rider_name` / `rider_id` / `rider_phone_hash` and both DDL sources file all three
--       under proxy booking (P-01/P-03) — and `ride.yaml` says a package ride has "no rider at
--       all", so overloading them would make `RideDetail.riderId` a claim about somebody who is
--       not the rider. Two columns of their own, and the CHECKs on `kind` are untouched.
--
--   (b) **`recipient_phone` is stored in the clear, unlike `rider_phone_hash`.** P-03 hashes the
--       *unregistered proxy rider's* number because nothing in the platform ever has to dial it —
--       the booker does. The recipient is the opposite case: AL-21 must SMS them and AL-33 must
--       let the driver ring them, and neither is possible from a digest. Hashing it would leave
--       both requirements unimplementable, so the number is kept exactly as `iam.users.phone`
--       keeps one, and PDPA erasure reaches it through `rides.rides` like every other ride column.
--       `recipient_name` is nullable because D3' marks neither field required.
--
--   Deliberately NOT added: a `recipient_id`. Whether the number belongs to an account is AL-21's
--   branch and it is notification-svc's to take at the moment it sends — resolving it at booking
--   time would freeze an answer that can change between the booking and the delivery, and iam-svc
--   already owns the lookup (`GET /v1/users/lookup`, P-03).
--
--   Deliberately NOT added: a `rides.timers` row for `location_request_expiry` — see (c).
-- =====================================================================================

ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS recipient_name TEXT;

ALTER TABLE rides.rides
  ADD COLUMN IF NOT EXISTS recipient_phone TEXT;

-- A package must be deliverable to somebody: AL-21's notification and AL-33's call button both
-- need the number, so a `kind=package` row without one is a delivery nobody can complete.
-- Written as `kind <> 2 OR ...` to match ck_rides_package_complete (0601) and to leave every
-- passenger and proxy row untouched.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'rides.rides'::regclass
                    AND conname = 'ck_rides_package_recipient') THEN
    ALTER TABLE rides.rides
      ADD CONSTRAINT ck_rides_package_recipient
      CHECK (kind <> 2 OR recipient_phone IS NOT NULL);
  END IF;
END $$;

COMMENT ON COLUMN rides.rides.recipient_name IS
  'Package recipient (P-06). NULL when the sender did not name one; D3'' RideRequest marks it optional.';
COMMENT ON COLUMN rides.rides.recipient_phone IS
  'Package recipient MSISDN in E.164, stored in the clear because AL-21 SMSes it and AL-33 dials it. Contrast rider_phone_hash (P-03), which nothing ever has to reach.';

-- (c) **The location-request TTL has no timer row, and cannot have one.** ADD §11.15 asks for
--     `rides.timers kind='location_request_expiry' fire_at=now()+5min`, but `rides.timers.ride_id`
--     is NOT NULL with a foreign key onto `rides.rides` (0605) and a location request is issued
--     *before* the ride exists — `rides.location_requests.ride_id` is nullable for exactly that
--     reason (0606). The durable deadline is therefore the request row itself:
--     `issued_at + ttl_seconds`, which is the same durable-row-decides property R-04 asks for.
--     This index is what makes "the next request to expire" a scan of the due ones rather than of
--     the booker history `ix_location_requests_booker` serves. Raised in the C037 handoff.
--     Both live states are in the predicate. AL-45 supersedes ADD §11.15's "unregistered ⇒ the
--     booker falls back and the request is over": the rider is SMSed a `pickup_confirm` link and
--     answers through public-bff, so a `RiderNotRegistered` request is open on another channel and
--     runs down the same 300 s clock.
CREATE INDEX IF NOT EXISTS ix_location_requests_due
  ON rides.location_requests(issued_at)
  WHERE state IN ('Pending', 'RiderNotRegistered');

COMMENT ON INDEX rides.ix_location_requests_due IS
  'The P-02 300 s expiry sweep (ADD §11.15, AL-45). Partial on the two live states because a resolved request is never due again.';
