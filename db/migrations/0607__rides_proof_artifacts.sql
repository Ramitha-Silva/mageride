-- =====================================================================================
-- 0607 — rides: delivery and payment proof artifacts
-- Source: server_db_schema.md §5, §25 · D4' §5 + Δ 2026-07-05 #2 · ADD §9.1 · P-10, AL-47
--
-- 365-day retention by default, PDPA-erasable (pdpa.requests, C005, E-06).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.proof_artifacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  -- The base §5 DDL lists three kinds; the Δ 2026-07-05 #2 change set (AL-47) adds
  -- 'qr_receipt' — the passenger's optional receipt screenshot attached to a driver-QR
  -- payment claim, referenced by fares.ride_payments.qr_claim_artifact_id (C005).
  kind TEXT NOT NULL CONSTRAINT ck_proof_artifacts_kind
    CHECK (kind IN ('delivery_photo','signature','pickup_photo','qr_receipt')),
  storage_url TEXT NOT NULL,
  sha256 BYTEA NOT NULL,                                      -- tamper evidence for disputes
  captured_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  captured_geo GEOGRAPHY(POINT,4326));

CREATE INDEX IF NOT EXISTS ix_proof_ride ON rides.proof_artifacts(ride_id);

COMMENT ON TABLE rides.proof_artifacts IS
  'Delivery proof (P-10) and driver-QR payment evidence (AL-47). 365-day retention, PDPA-erasable.';
