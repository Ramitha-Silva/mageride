-- =====================================================================================
-- 0601 — rides: the Mode C ride aggregate
-- Source: server_db_schema.md §5 · D4' §5 · ADD §9.1 + Appendix B.2 · D5' §6/§6.1
--         · R-01, R-02, R-10, R-11, R-18, O2, P-01, P-04, P-06, P-07, P-08
--
-- R-01 fence: rides.* is Mode C ONLY and ride-svc is its sole writer. Mode A/B tracking is
-- trips.sessions (0501), owned by trip-state-svc.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.rides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  client_request_id UUID NOT NULL,                            -- idempotency partner (R-18)

  -- v2.2 proxy booking (P-01, P-03) ---------------------------------------------------
  booker_id UUID NOT NULL REFERENCES iam.users(id),           -- = passenger unless proxy
  rider_id UUID REFERENCES iam.users(id),                     -- NULL if unregistered
  rider_phone_hash BYTEA,                                     -- hashed PII, unregistered rider
  rider_name TEXT,
  is_proxy BOOLEAN NOT NULL DEFAULT false,
  kind SMALLINT NOT NULL DEFAULT 0
    CONSTRAINT ck_rides_kind CHECK (kind IN (0,1,2)),         -- 0=passenger,1=proxy,2=package (P-06)

  vehicle_type TEXT NOT NULL,                                 -- requested tier
  pickup_geo GEOGRAPHY(POINT,4326) NOT NULL,
  dropoff_geo GEOGRAPHY(POINT,4326) NOT NULL,

  -- The 18 states of D5' §6 / ADD Appendix B.2, verbatim. ride-svc is the sole writer and
  -- every move is audited in rides.transitions (0602).
  state TEXT NOT NULL DEFAULT 'Requested' CONSTRAINT ck_rides_state CHECK (state IN
    ('Requested','Matching','Offered','Accepted','DriverArrived','InProgress','Completed',
     'PaymentPending','Paid','CashSettled','CashOnDeliveryCollected','Disputed',
     'CancelledByRiderBeforeAccept','CancelledByRiderAfterAccept','CancelledByDriver',
     'ExpiredNoDriver','NoShowRider','NoShowDriver')),

  accepted_driver_id UUID REFERENCES iam.users(id),
  accepted_vehicle_id UUID REFERENCES registry.vehicles(id),
  -- No FK: dispatch.offers.ride_id already points here, and a second edge would make the two
  -- tables mutually dependent — an offer could then never be inserted first. Both specs print
  -- it bare for the same reason.
  current_offer_id UUID,
  offer_expires_at TIMESTAMPTZ,                               -- 15 s TTL hint (D5' §3.5)
  dispatch_algorithm_version SMALLINT,                        -- R-11

  -- v2.2 package delivery (P-06, P-07) ------------------------------------------------
  package_size CHAR(1)
    CONSTRAINT ck_rides_package_size CHECK (package_size IN ('S','M','L')),
  package_description TEXT,
  pickup_otp_hash BYTEA,                                      -- HMAC-SHA256 of a 4-digit OTP
  delivery_otp_hash BYTEA,
  pickup_otp_attempts SMALLINT NOT NULL DEFAULT 0,            -- max 5 → admin queue
  delivery_otp_attempts SMALLINT NOT NULL DEFAULT 0,

  -- Booking-time choice (D3' POST /v1/rides/request). 'cod' is package-only; the driver-QR
  -- method 'scan_driver_qr' (AL-22) is a *settlement* choice made later and lives on
  -- fares.ride_payments.method (C005), not here.
  payment_method TEXT NOT NULL DEFAULT 'cash'
    CONSTRAINT ck_rides_payment_method
    CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),

  version BIGINT NOT NULL DEFAULT 0,                          -- optimistic concurrency (R-02)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  terminal_at TIMESTAMPTZ,

  -- A package cannot exist without its size and both OTPs (P-06, P-07).
  CONSTRAINT ck_rides_package_complete CHECK (
    kind <> 2 OR (package_size IS NOT NULL
                  AND pickup_otp_hash IS NOT NULL
                  AND delivery_otp_hash IS NOT NULL)),
  -- A proxy ride must name its rider and be able to reach them (P-01, P-03).
  CONSTRAINT ck_rides_proxy_identity CHECK (
    is_proxy = false OR (rider_name IS NOT NULL
                         AND (rider_id IS NOT NULL OR rider_phone_hash IS NOT NULL))));

-- R-18: POST /v1/rides/request is idempotent on (passenger, clientRequestId) — a retry returns
-- the existing ride instead of booking a second one.
CREATE UNIQUE INDEX IF NOT EXISTS ux_rides_idem
  ON rides.rides(passenger_id, client_request_id);

-- One open ride per rider. The exempt list is the terminal set of D5' §6 — note it also
-- exempts 'Completed', so the guard lifts at Completed and re-applies at PaymentPending
-- (see the C004 handoff note; landed exactly as both specs print it).
CREATE UNIQUE INDEX IF NOT EXISTS ux_rides_open_passenger
  ON rides.rides(passenger_id)
  WHERE state NOT IN ('Completed','Paid','CashSettled','CashOnDeliveryCollected','Disputed',
    'CancelledByRiderBeforeAccept','CancelledByRiderAfterAccept','CancelledByDriver',
    'ExpiredNoDriver','NoShowRider','NoShowDriver');

-- O2 + R-10: two drivers can never both win an offer. This index is the authoritative half of
-- the atomic accept (D5' §6.1); the Redis lock is only the fast path.
CREATE UNIQUE INDEX IF NOT EXISTS ux_rides_driver_busy
  ON rides.rides(accepted_driver_id)
  WHERE state IN ('Accepted','DriverArrived','InProgress','PaymentPending');

CREATE INDEX IF NOT EXISTS ix_rides_driver
  ON rides.rides(accepted_driver_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_rides_passenger_hist
  ON rides.rides(passenger_id, created_at DESC);

SELECT public.attach_set_updated_at('rides','rides');

COMMENT ON TABLE rides.rides IS
  'Mode C ride aggregate (R-01). ride-svc is the sole writer. Kind-agnostic state machine: passenger/proxy/package traverse the same 18 states (ADD Appendix B.2 invariant 6).';
COMMENT ON COLUMN rides.rides.version IS
  'Optimistic concurrency (R-02). The accept is a conditional UPDATE ... AND version=:expected; rowcount 0 means another driver won.';
COMMENT ON INDEX rides.ux_rides_idem IS 'R-18: idempotent POST /v1/rides/request.';
COMMENT ON INDEX rides.ux_rides_driver_busy IS
  'O2 + R-10: at most one non-terminal ride per driver in Accepted/DriverArrived/InProgress/PaymentPending.';
