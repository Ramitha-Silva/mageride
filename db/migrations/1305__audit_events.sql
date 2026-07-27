-- =====================================================================================
-- 1305 — audit: immutable admin log
-- Source: server_db_schema.md §15 + §23 (Δ 2026-06-28) · D4' §11-16 · ADD §9.1
--         D-35, AL-39…AL-42
--
-- Append-only. The REVOKE below is the mechanism both specs print; note what it does and
-- does not do — see the comment on it.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS audit.events (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- Nullable: a system-initiated action (expiry auto-suspend, scheduled job) has no actor.
  actor_id UUID,
  -- Free text. Known values include the ordinary mutations plus the two read-access
  -- actions AL-39/AL-42 added: DOC_VIEW (a full-size document opened in SCR-AP-003b) and
  -- PII_READ (a passenger or driver directory detail opened).
  action TEXT NOT NULL,
  entity_type TEXT,
  entity_id UUID,
  before JSONB,
  after JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit.events(entity_type, entity_id, ts DESC);
-- "What did this officer do?" — the auditor's primary query, and the one AL-39/AL-42
-- read-access auditing exists to answer.
CREATE INDEX IF NOT EXISTS ix_audit_actor ON audit.events(actor_id, ts DESC);
CREATE INDEX IF NOT EXISTS ix_audit_action ON audit.events(action, ts DESC);

-- Append-only, as printed in both specs. This revokes the PUBLIC grant only: it stops a
-- role that holds no explicit privilege, not the table owner or a superuser. Real
-- immutability is the deployment's job — the service role must be granted INSERT and
-- SELECT and nothing else (D7' §13). Recorded here so nobody reads the REVOKE as more
-- than it is.
REVOKE UPDATE, DELETE ON audit.events FROM PUBLIC;

COMMENT ON TABLE audit.events IS
  'Immutable admin action log (D-35), 7-year retention where regulated. Append-only: the service role holds INSERT/SELECT only, and the REVOKE here is a backstop rather than the guarantee.';
COMMENT ON COLUMN audit.events.action IS
  'Includes the AL-39/AL-42 read-access actions DOC_VIEW and PII_READ — viewing a document or a directory detail is itself auditable.';
