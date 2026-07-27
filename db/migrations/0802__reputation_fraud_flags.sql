-- =====================================================================================
-- 0802 — reputation: anti-collusion / ride-farming flags
-- Source: server_db_schema.md §7 · D4' §7 · ADD §9.1 · E-07
--
-- Append-only detection output. kind and detail are open on purpose: E-07 heuristics are
-- expected to grow, and a new detector must not need a migration.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS reputation.fraud_flags (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kind TEXT NOT NULL,                                         -- e.g. repeat_pair, self_ride, gps_teleport
  subject_id UUID,                                            -- the flagged user
  related_id UUID,                                            -- counterparty, where the signal is a pair
  detail JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_fraud_flags_subject
  ON reputation.fraud_flags(subject_id, ts DESC);
CREATE INDEX IF NOT EXISTS ix_fraud_flags_kind
  ON reputation.fraud_flags(kind, ts DESC);

COMMENT ON TABLE reputation.fraud_flags IS
  'E-07 anti-collusion / ride-farming signals. Raising a flag never blocks a user by itself — reputation-svc decides whether it moves reputation.block_states.';
