-- =====================================================================================
-- 0108 — iam: the proxy-booking phone-lookup log
-- Source: D3' iam-svc GET /v1/users/lookup · ADD §12.5 P-03 · E-06 · D-35
--
-- ⚠ Spec gap — micro-change-set, raised in the C027 handoff. P-03 says the phone number of
--   an unregistered rider "is hashed at rest, retained only until ride terminal", and models
--   that as `rides.rides.rider_phone_hash` (0601) — the number the *booker typed*, held by
--   ride-svc for the life of the ride. It says nothing about the lookup that decides whether
--   there is a rider account at all.
--
--   `GET /v1/users/lookup` is a registration oracle: anything that can call it can ask "does
--   +94 77 xxx xxxx have a MageRide account". D-35 wants that answerable after the fact and
--   E-06 wants the answer to contain no PII. Both are satisfied by the same row — the number
--   is reduced to an HMAC before it reaches this table and the clear value is never written.
--   The hash is keyed with `Auth:PhoneHashKey`, so a leaked table is not an offline search of
--   the ~10^8 Sri Lankan mobile space.
--
--   D4' §1 should carry this table, or D3' should say the lookup is not logged. Doing neither
--   leaves the one endpoint on the platform that answers a question about a person who never
--   signed up with no record that it was asked.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS iam.phone_lookups (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- HMAC-SHA256(E.164 number) under Auth:PhoneHashKey. NOT the number, and deliberately not
  -- reversible: this column exists to correlate repeats, never to recover a phone number.
  phone_hash BYTEA NOT NULL,
  -- The answer that was given. A registered lookup also names the account, because that is a
  -- fact iam.users already holds in the clear; an unregistered one names nothing at all.
  registered BOOLEAN NOT NULL,
  user_id UUID REFERENCES iam.users(id) ON DELETE SET NULL,
  -- Which service asked. mTLS peer identity once C042 lands a mesh; the interim shared-secret
  -- caller names itself, and an unnamed caller is recorded as unknown rather than dropped.
  caller TEXT,
  looked_up_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_phone_lookups_identity CHECK (registered OR user_id IS NULL));

CREATE INDEX IF NOT EXISTS ix_phone_lookups_hash ON iam.phone_lookups(phone_hash, looked_up_at DESC);
-- The retention sweep's index: oldest first, so a job can delete by age without a sort.
CREATE INDEX IF NOT EXISTS ix_phone_lookups_age ON iam.phone_lookups(looked_up_at);

COMMENT ON TABLE iam.phone_lookups IS
  'Audit of GET /v1/users/lookup, the P-03 proxy-booking registration oracle. Stores an HMAC of the number and never the number itself (E-06). Retention is a sweep, not a cascade — the row outlives the account it may have named.';
COMMENT ON COLUMN iam.phone_lookups.phone_hash IS
  'HMAC-SHA256 of the E.164 number keyed with Auth:PhoneHashKey. Correlates repeated lookups of one number without storing it.';
