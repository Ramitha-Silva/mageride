-- =====================================================================================
-- 0102 — iam: device binding, refresh-token sessions, OTP rate limiting
-- Source: server_db_schema.md §1 · D4' §1 · D-29, D-30, D-32, AL-08
-- =====================================================================================

CREATE TABLE IF NOT EXISTS iam.devices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  platform TEXT NOT NULL CHECK (platform IN ('android','ios')),
  fcm_apns_token TEXT,
  keystore_pubkey TEXT,                                       -- Android Keystore / iOS Secure Enclave
  attestation_verified_at TIMESTAMPTZ,                        -- Play Integrity / App Attest (D-30)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_devices_user ON iam.devices(user_id);
SELECT public.attach_set_updated_at('iam','devices');

-- Refresh-token store (D-29). The access token is a stateless RS256 JWT and is not stored;
-- this row is the revocation record, mirrored into Redis refresh:{jti} for O(1) lookup.
CREATE TABLE IF NOT EXISTS iam.sessions (
  jti UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  device_id UUID NOT NULL REFERENCES iam.devices(id) ON DELETE CASCADE,
  app TEXT NOT NULL DEFAULT 'passenger' CHECK (app IN ('passenger','driver')),
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_used_at TIMESTAMPTZ,
  revoked_at TIMESTAMPTZ);

-- AL-08 / US-1.12: single active device PER APP. A new-device login revokes only that app's
-- prior session, so one person can run the Driver and Passenger apps at the same time. The
-- partial unique index is what makes that an invariant rather than a convention.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sessions_active_app
  ON iam.sessions(user_id, app) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_sessions_user ON iam.sessions(user_id);
CREATE INDEX IF NOT EXISTS ix_sessions_device ON iam.sessions(device_id);

COMMENT ON INDEX iam.ux_sessions_active_app IS
  'AL-08: at most one unrevoked session per (user, app).';

-- Token-bucket OTP rate limit (D-32: 60 s resend cooldown, 5 per hour). The Redis bucket
-- is the hot path; these rows are the durable attempt/verification record.
CREATE TABLE IF NOT EXISTS iam.otp_attempts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  phone TEXT NOT NULL,
  auth_id UUID NOT NULL,
  otp_hash BYTEA NOT NULL,                                    -- never the OTP itself
  attempts SMALLINT NOT NULL DEFAULT 0,
  expires_at TIMESTAMPTZ NOT NULL,
  verified BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_otp_phone ON iam.otp_attempts(phone, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_otp_auth ON iam.otp_attempts(auth_id);
