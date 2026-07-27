-- =====================================================================================
-- 0901 — safety: public share tokens
-- Source: server_db_schema.md §8 + §24 (Δ 2026-07-05) · D4' §8 + Δ 2026-07-05
--         ADD §9.1 · D-34, P-09, AL-44, AL-45
--
-- The single credential behind every unauthenticated public surface: the D-34 trip-share
-- link, the P-09 package-recipient link, and the two SCR-WT web scopes added by AL-44.
-- Landed in its final (post-Δ) shape rather than as base DDL plus ALTERs — the change
-- sets are migration history, not a sequence this repo has to replay.
--
-- Ordered first in the 09xx range because safety.sos_events.share_token references it.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS safety.trip_share_tokens (
  -- The token itself is the primary key: every lookup is by the value in the URL, and a
  -- surrogate id would only add a second thing to keep secret.
  token TEXT PRIMARY KEY,
  -- Polymorphic across rides.rides (Mode C) and trips.sessions (Mode A/B), so no FK —
  -- the same shape both DDL sources print. NULL only for 'pickup_confirm', which is
  -- minted while the ride is still a draft (AL-44).
  trip_id UUID,
  scope TEXT NOT NULL CONSTRAINT ck_trip_share_tokens_scope
    CHECK (scope IN ('trip_view','package_recipient','proxy_rider','pickup_confirm')),
  -- 'pickup_confirm' addresses a location request rather than a trip (P-02/P-13).
  location_request_id UUID REFERENCES rides.location_requests(id) ON DELETE CASCADE,
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ,                                     -- burned on confirm/decline/expiry
  -- Public-surface metering (AL-44): a shared link is unauthenticated, so the only
  -- forensic trail for an abused token is how often and how recently it was redeemed.
  last_access_at TIMESTAMPTZ,
  access_count INTEGER NOT NULL DEFAULT 0 CHECK (access_count >= 0),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_trip_share_tokens_subject CHECK (
       (scope =  'pickup_confirm' AND location_request_id IS NOT NULL)
    OR (scope <> 'pickup_confirm' AND trip_id IS NOT NULL)));

-- Revocation is per trip: every live token for a ride is burned when it completes
-- (proxy_rider TTL = completion, package_recipient TTL = delivery + 1 h — D6 I-29.2).
CREATE INDEX IF NOT EXISTS ix_trip_share_tokens_trip
  ON safety.trip_share_tokens(trip_id) WHERE revoked_at IS NULL;
-- The expiry sweep and the pickup_confirm burn-on-timeout both scan by deadline.
CREATE INDEX IF NOT EXISTS ix_trip_share_tokens_expiry
  ON safety.trip_share_tokens(expires_at) WHERE revoked_at IS NULL;

COMMENT ON TABLE safety.trip_share_tokens IS
  'Capability tokens for the unauthenticated public surfaces: D-34 trip share, P-09 package recipient, and the AL-44 proxy_rider / pickup_confirm web scopes. Never returned to an authenticated client API — notification-svc mints them server-side and embeds them in SMS (D6 I-29.2).';
COMMENT ON COLUMN safety.trip_share_tokens.trip_id IS
  'Mode C rides.rides.id or Mode A/B trips.sessions.id. Deliberately unconstrained: the referent is polymorphic, exactly as both DDL sources print it.';
COMMENT ON COLUMN safety.trip_share_tokens.access_count IS
  'AL-44 public-surface metering. Incremented on every redemption; the rate limit itself lives in the gateway, this is the forensic record.';
