-- =====================================================================================
-- 0107 — iam: portal credentials, federated identities, and the AL-37 lock-out
-- Source: D3' iam-svc POST /v1/auth/password · /v1/auth/google · /v1/auth/apple ·
--         /v1/admin/auth/login (Δ 2026-06-28 item 5) · AL-07, AL-37, AL-08 · ADD §12.1
--
-- ⚠ Spec gap — micro-change-set, raised in the C026 handoff. AL-07 gives the Admin Portal
--   password-or-Google sign-in and the Fleet Portal email+password / Google / Apple, and AL-37
--   replaces the removed MFA step with a **failed-attempt lock-out**. Neither D4' §1 nor
--   server_db_schema.md §1 has anywhere to put any of it:
--
--   (a) **No password is storable.** `iam.users` carries `email` and nothing else — no hash, no
--       algorithm, no salt. Two of the four sign-in surfaces the ADD lists are therefore
--       unimplementable as the schema stands. `iam.user_credentials` is a 1:1 side table rather
--       than columns on `iam.users` so that reading a profile never reads a verifier, and so an
--       app account (Phone OTP, AL-07) simply has no row.
--
--   (b) **The lock-out has no counter.** AL-37 names "failed-attempt lock-out" as one of the
--       three controls compensating for the removed second factor, and D3' maps it to
--       `423 otp-locked` on both password routes. A counter in Redis alone would be wrong: a
--       flush would hand an attacker a clean slate on every internal account at once, which is
--       precisely the guarantee the control exists to give. It lives with the verifier it
--       counts, and clears on a successful sign-in.
--
--   (c) **A Google or Apple identity has nowhere to bind.** Matching on `iam.users.email` alone
--       would mean anybody who can get a provider to assert an address owns the account, and it
--       cannot survive a user changing their provider-side address. The provider's immutable
--       `sub` is the identity; `iam.federated_identities` is the binding, unique per
--       `(provider, subject)`.
--
--   (d) **A portal session cannot be stored.** `iam.sessions.app` is
--       `CHECK (app IN ('passenger','driver'))` and `iam.devices.platform` is
--       `CHECK (platform IN ('android','ios'))`, so signing in from a browser has no legal row —
--       yet ADD §12.1 issues portals the same RS256 access + opaque refresh pair as the apps and
--       AL-37 names **session binding** as a compensating control. Both CHECKs are widened:
--       `app` gains `admin` and `fleet`, `platform` gains `web`. The AL-08 partial unique index
--       is left exactly as it is, and that is the point — it now also means "one live Admin
--       Portal session per person", which is what session binding buys.
--
--   (e) **`fcmToken` on otp/request had nowhere to wait.** C020 accepted and dropped it
--       (handoff gap (e)): its home is `iam.devices.fcm_apns_token`, which cannot exist until
--       verify identifies the user. It now waits on the attempt row.
--
--   **D4' §1 should carry all of it.** Nothing here is a new concept — every one is a fact the
--   ADD already states and the contract already exposes an endpoint for.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (a)/(b) Password verifier + the AL-37 lock-out that replaced MFA
-- -------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS iam.user_credentials (
  user_id UUID PRIMARY KEY REFERENCES iam.users(id) ON DELETE CASCADE,
  -- PHC-style encoded verifier: algorithm, parameters, salt and hash in one self-describing
  -- string, so the work factor can be raised without a migration and an old row still verifies.
  password_hash TEXT NOT NULL,
  password_updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- AL-37 compensating control. Counts consecutive failures; a success zeroes it.
  failed_attempts SMALLINT NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
  locked_until TIMESTAMPTZ,                                   -- NULL = not locked
  last_login_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('iam','user_credentials');

COMMENT ON TABLE iam.user_credentials IS
  'Password verifier for the Admin and Fleet portals (AL-07). Apps are Phone OTP only and have no row. failed_attempts/locked_until are the AL-37 lock-out that replaced the MFA step — durable on purpose, so a Redis flush cannot clear every internal account''s counter at once.';
COMMENT ON COLUMN iam.user_credentials.password_hash IS
  'PHC-style encoded verifier: algorithm, parameters, salt and hash in one dollar-delimited string. Self-describing so the work factor can be raised without invalidating existing rows. (Spelled out rather than shown, because DbUp reads a dollar-delimited token as a variable.)';

-- -------------------------------------------------------------------------------------
-- (c) Google / Apple identity binding
-- -------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS iam.federated_identities (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  provider TEXT NOT NULL CHECK (provider IN ('google','apple')),
  -- The provider's `sub`. Immutable at the provider and the only stable identity it asserts —
  -- an email address is neither (AL-07 lists Google on two surfaces and Apple on one).
  subject TEXT NOT NULL,
  email TEXT,                                                 -- as asserted at link time, for audit
  linked_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_login_at TIMESTAMPTZ);

-- One MageRide account per provider identity, and at most one identity per provider per account.
CREATE UNIQUE INDEX IF NOT EXISTS ux_federated_provider_subject
  ON iam.federated_identities(provider, subject);
CREATE UNIQUE INDEX IF NOT EXISTS ux_federated_user_provider
  ON iam.federated_identities(user_id, provider);

COMMENT ON TABLE iam.federated_identities IS
  'Google (Admin + Fleet) and Apple (Fleet) sign-in bindings (AL-07). Keyed on the provider''s immutable subject, never on the asserted email — an address can be changed at the provider and re-asserted by somebody else.';

-- -------------------------------------------------------------------------------------
-- (d) Portal sessions: widen the two CHECKs, leave the AL-08 index alone
-- -------------------------------------------------------------------------------------

-- The C003 constraints are inline and therefore system-named. Dropped by that name and replaced
-- with explicit ones, so this script is re-runnable and a later change has a name to target.
ALTER TABLE iam.sessions DROP CONSTRAINT IF EXISTS sessions_app_check;
ALTER TABLE iam.sessions DROP CONSTRAINT IF EXISTS ck_sessions_app;
ALTER TABLE iam.sessions
  ADD CONSTRAINT ck_sessions_app CHECK (app IN ('passenger','driver','admin','fleet'));

COMMENT ON COLUMN iam.sessions.app IS
  'The surface this session belongs to: passenger | driver (apps, Phone OTP) or admin | fleet (portals). ux_sessions_active_app therefore gives one live session per person per surface — AL-08 for the apps, and the "session binding" AL-37 names as a compensating control for the portals.';

ALTER TABLE iam.devices DROP CONSTRAINT IF EXISTS devices_platform_check;
ALTER TABLE iam.devices DROP CONSTRAINT IF EXISTS ck_devices_platform;
ALTER TABLE iam.devices
  ADD CONSTRAINT ck_devices_platform CHECK (platform IN ('android','ios','web'));

COMMENT ON COLUMN iam.devices.platform IS
  'android | ios from the gateway''s X-Platform (D-31), or web for a portal browser. A browser''s device_key is derived from its user-agent, which is what binds a portal session to a client (AL-37).';

-- -------------------------------------------------------------------------------------
-- (e) Carry the push token from otp/request to the device row verify creates
-- -------------------------------------------------------------------------------------

ALTER TABLE iam.otp_attempts
  ADD COLUMN IF NOT EXISTS fcm_token TEXT;

COMMENT ON COLUMN iam.otp_attempts.fcm_token IS
  'The optional fcmToken from POST /v1/auth/otp/request. It cannot be written to iam.devices.fcm_apns_token until verify identifies the user, so it waits here (C020 handoff gap (e)).';
