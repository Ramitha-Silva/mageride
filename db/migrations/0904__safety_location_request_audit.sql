-- =====================================================================================
-- 0904 — safety: proxy location-request audit
-- Source: server_db_schema.md §8 · D4' §8 · ADD §9.1 · P-12
--
-- Every outcome of the booker→rider GPS round-trip (rides.location_requests, C004) is
-- recorded here, including the declines and the requests that never reached anyone.
-- The rate limit itself is a Redis token bucket (5/h, 30/day per booker — D6 §7.4);
-- this table is the durable record behind an abuse investigation.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS safety.location_request_audit (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  booker_id UUID NOT NULL REFERENCES iam.users(id),
  -- The rider is frequently unregistered, so the subject is stored hash-only (P-03, §0 PII).
  rider_phone_hash BYTEA NOT NULL,
  -- rides.location_requests.request_id — the public handle, not the surrogate id. Left
  -- bare in both specs: the audit row must survive the request row being purged.
  request_id UUID NOT NULL,
  decision TEXT NOT NULL CONSTRAINT ck_location_request_audit_decision
    CHECK (decision IN ('Confirmed','Declined','Expired','NotRegistered')),
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

-- P-12 forensics run per booker over a window, and per subject when a rider complains.
CREATE INDEX IF NOT EXISTS ix_locreq_audit_booker
  ON safety.location_request_audit(booker_id, ts DESC);
CREATE INDEX IF NOT EXISTS ix_locreq_audit_subject
  ON safety.location_request_audit(rider_phone_hash, ts DESC);

COMMENT ON TABLE safety.location_request_audit IS
  'Durable outcome log for proxy location requests (P-12). Declines matter most: a booker who repeatedly pings a rider who keeps declining is the abuse pattern this exists to surface.';
