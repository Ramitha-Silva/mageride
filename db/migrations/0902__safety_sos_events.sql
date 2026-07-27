-- =====================================================================================
-- 0902 — safety: SOS events
-- Source: server_db_schema.md §8 + §24 (Δ 2026-07-05) · D4' §8 + Δ 2026-07-05
--         ADD §9.1 · D-33, AL-44, US-12.11, US-25.5
--
-- Final (post-Δ) shape: user_id is nullable because a web SOS raised from an SCR-WT page
-- carries a share token instead of an app identity.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS safety.sos_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID REFERENCES iam.users(id),                      -- NULL for a web guest (AL-44)
  role TEXT NOT NULL CONSTRAINT ck_sos_events_role
    CHECK (role IN ('passenger','driver')),
  ride_id UUID,                                               -- polymorphic ride/session, bare in both specs
  lat DOUBLE PRECISION NOT NULL CHECK (lat BETWEEN -90 AND 90),
  lng DOUBLE PRECISION NOT NULL CHECK (lng BETWEEN -180 AND 180),
  emergency_contact TEXT,
  -- The D-33 dual-gateway outcome: which gateway was tried and what it returned. Free
  -- text in both specs — safety-svc (C052) owns the vocabulary.
  sms_status TEXT,
  primary_gateway TEXT,
  secondary_gateway TEXT,
  admin_acked_at TIMESTAMPTZ,
  source TEXT NOT NULL DEFAULT 'app' CONSTRAINT ck_sos_events_source
    CHECK (source IN ('app','web')),
  share_token TEXT REFERENCES safety.trip_share_tokens(token),
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- An SOS with neither an identity nor a token could never be routed to a responder.
  CONSTRAINT ck_sos_events_actor CHECK (user_id IS NOT NULL OR share_token IS NOT NULL));

CREATE INDEX IF NOT EXISTS ix_sos_user ON safety.sos_events(user_id, ts DESC);
-- The admin SOS queue (SCR-AP-005) is "unacknowledged, newest first" across all users.
CREATE INDEX IF NOT EXISTS ix_sos_unacked
  ON safety.sos_events(ts DESC) WHERE admin_acked_at IS NULL;

COMMENT ON TABLE safety.sos_events IS
  'Passenger and driver panic button (D-33, US-12.11). A web-originated SOS (AL-44/US-25.5) identifies itself by share token instead of an iam.users row.';
COMMENT ON COLUMN safety.sos_events.share_token IS
  'Kept by AL-48, which dropped only the call-side token column. This is the sole identity a web SOS carries.';
