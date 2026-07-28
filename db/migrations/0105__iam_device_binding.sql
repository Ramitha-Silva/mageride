-- =====================================================================================
-- 0105 — iam: bind a device row and an OTP attempt to the client's device identifier
-- Source: D3' §0 "Auth" (device_id claim, AL-08) · D3' iam-svc POST /v1/auth/otp/request
--         and /verify (409 device-mismatch) · D-29, D-30
--
-- ⚠ Spec gap — micro-change-set, raised in the C020 handoff. Three columns the auth contract
--   needs are absent from both D4' §1 and server_db_schema.md §1:
--
--   (a) `iam.devices` has no column for the client's `deviceId`. The contract makes it a
--       required, "stable per-install device identifier" on otp/request, D3' §0 puts it in the
--       access token as the `device_id` claim, and AL-08 binds a session to it — but the table
--       keys only on a generated UUID, so nothing records *which* handset a row describes.
--       Without it a second sign-in from the same install creates a second device row.
--   (b)/(c) `iam.otp_attempts` records neither the `deviceId` nor the app the OTP was requested
--       for. Verify must answer `409 device-mismatch` when the two device ids differ, and must
--       know whether to open a passenger or a driver session (AL-08) — both are attributes of
--       the *attempt*, and Redis is the wrong home for them because a flush would strand every
--       in-flight login.
--
--   Columns are nullable so the C003 rows and constraints stay valid; iam-svc always writes them.
-- =====================================================================================

ALTER TABLE iam.devices
  ADD COLUMN IF NOT EXISTS device_key TEXT;                   -- client deviceId, ≤128 (D3' contract)

-- One device row per (user, install). Partial so the pre-C020 rows, which have no key, do not
-- collide with each other on NULL.
CREATE UNIQUE INDEX IF NOT EXISTS ux_devices_user_key
  ON iam.devices(user_id, device_key) WHERE device_key IS NOT NULL;

COMMENT ON COLUMN iam.devices.device_key IS
  'The client-supplied deviceId this row describes (D3'' otp/request). Carried in the access token as the device_id claim and bound to the session (AL-08).';

ALTER TABLE iam.otp_attempts
  ADD COLUMN IF NOT EXISTS device_id TEXT;                    -- deviceId presented at request time
ALTER TABLE iam.otp_attempts
  ADD COLUMN IF NOT EXISTS app TEXT;                          -- passenger | driver (AL-08)

-- ADD CONSTRAINT has no IF NOT EXISTS; guard it so the script re-runs cleanly with the journal
-- disabled (db/CLAUDE.md, and the pattern 1401 already uses).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'iam.otp_attempts'::regclass
                    AND conname = 'ck_otp_attempts_app') THEN
    ALTER TABLE iam.otp_attempts
      ADD CONSTRAINT ck_otp_attempts_app CHECK (app IS NULL OR app IN ('passenger','driver'));
  END IF;
END $$;

COMMENT ON COLUMN iam.otp_attempts.device_id IS
  'deviceId sent to POST /v1/auth/otp/request. Verify answers 409 device-mismatch when the two differ.';
COMMENT ON COLUMN iam.otp_attempts.app IS
  'Which app requested the OTP. Becomes iam.sessions.app and the token''s app claim (AL-08).';
