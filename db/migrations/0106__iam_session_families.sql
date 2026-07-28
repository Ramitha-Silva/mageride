-- =====================================================================================
-- 0106 — iam: refresh-token lineage, so replay detection cannot log the wrong device out
-- Source: D-29 (single-use rotating refresh) · D3' iam-svc POST /v1/auth/refresh
--         ("Presenting a spent token revokes the whole session family") · AL-08
--
-- ⚠ Spec gap — micro-change-set, raised in the C020 handoff.
--   D3' names a "session family" and makes replaying a spent refresh token revoke it, but
--   `iam.sessions` (D4' §1, server_db_schema.md §1) has no column linking a rotated session to
--   the one it replaced. Every session of a (user, app) therefore looks alike, and "revoke the
--   family" can only mean "revoke whatever is active for this (user, app)" — which is wrong in
--   the ordinary case and livelocks:
--
--     device A signs in · device B signs in, revoking A's session (AL-08) · A's background
--     refresh fires, presents its now-revoked token, and takes B's brand-new session with it ·
--     B signs in again · A refreshes again · …
--
--   With a family id, rotation carries the lineage forward and a fresh sign-in starts a new one,
--   so replaying a rotated-out token kills exactly its own successor and a superseded token from
--   an older sign-in kills nothing. That is what the D3' sentence has to mean to be safe.
-- =====================================================================================

ALTER TABLE iam.sessions
  ADD COLUMN IF NOT EXISTS family_id UUID;

-- Rows predating this column are each their own family; nothing was rotated into them.
UPDATE iam.sessions SET family_id = jti WHERE family_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_sessions_family ON iam.sessions(family_id) WHERE revoked_at IS NULL;

COMMENT ON COLUMN iam.sessions.family_id IS
  'Refresh-token lineage (D-29). A sign-in starts a family (family_id = jti); a rotation keeps it. Replaying a spent token revokes only its own family, never a session opened by a later sign-in.';
