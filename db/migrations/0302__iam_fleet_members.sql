-- =====================================================================================
-- 0302 — iam: org-scoped fleet sub-roles
-- Source: server_db_schema.md §1 · D4' §1 · AL-03
--
-- Numbered in the registry range even though the table lives in `iam`: it references
-- registry.fleets, so it cannot be created with the rest of iam in 01xx. The §1 listing
-- in server_db_schema.md is not runnable in its printed order for the same reason.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS iam.fleet_members (
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  fleet_role TEXT NOT NULL CHECK (fleet_role IN ('owner','manager','viewer')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (fleet_id, user_id));

CREATE INDEX IF NOT EXISTS ix_fleet_members_user ON iam.fleet_members(user_id);

COMMENT ON TABLE iam.fleet_members IS
  'Org-scoped fleet sub-roles (AL-03). Surfaces as the fleet_role JWT claim; owner > manager > viewer.';
