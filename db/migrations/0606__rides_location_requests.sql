-- =====================================================================================
-- 0606 — rides: proxy-booking GPS round-trip
-- Source: server_db_schema.md §5 · D4' §5 · ADD §9.1/§11.15 · P-02, P-03, P-12, P-13
--
-- A booker asks an unregistered or registered rider for their live position to use as the
-- pickup. The row exists before the ride does, which is why ride_id is nullable.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.location_requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id) ON DELETE CASCADE,  -- NULL while still a ride draft
  -- The client-facing handle (D3' POST /v1/location-requests → {requestId}) and the SignalR
  -- group key `booker:{bookerId}:loc-req:{requestId}` (P-13). Kept distinct from the surrogate
  -- id so the public handle can be rotated without touching FKs.
  request_id UUID NOT NULL UNIQUE,
  booker_id UUID NOT NULL REFERENCES iam.users(id),
  rider_id UUID REFERENCES iam.users(id),                     -- NULL if the rider is unregistered
  rider_phone_hash BYTEA,                                     -- hashed PII (P-03)
  state TEXT NOT NULL DEFAULT 'Pending'
    CONSTRAINT ck_location_requests_state
    CHECK (state IN ('Pending','Confirmed','Declined','Expired','RiderNotRegistered')),
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ttl_seconds INTEGER NOT NULL DEFAULT 300,                   -- 5 min, durable timer in rides.timers
  resolved_at TIMESTAMPTZ,
  resolved_geo GEOGRAPHY(POINT,4326),
  resolved_accuracy_m NUMERIC);

-- P-12 rate limiting counts a booker's recent requests (5/h, 30/d).
CREATE INDEX IF NOT EXISTS ix_location_requests_booker
  ON rides.location_requests(booker_id, issued_at DESC);
CREATE INDEX IF NOT EXISTS ix_location_requests_ride
  ON rides.location_requests(ride_id);

COMMENT ON TABLE rides.location_requests IS
  'Short-lived booker→rider GPS round-trip for proxy booking (P-02, P-03). safety.trip_share_tokens.location_request_id (C005, AL-44) points here for the pickup_confirm web scope.';
COMMENT ON COLUMN rides.location_requests.rider_phone_hash IS
  'The raw MSISDN is never stored for an unregistered rider (P-03). The decision audit is safety.location_request_audit (C005, P-12).';
