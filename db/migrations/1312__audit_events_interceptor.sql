-- =====================================================================================
-- 1312 — audit: the four columns the admin-bff interceptor records (C062)
-- Source: server_db_schema.md §15 · D6' §2.1/§2.2 · backend/contracts/admin-bff.yaml
--         #/components/schemas/AuditEvent · D-35, AL-39…AL-42
--
-- §15 prints eight columns and the D-35 interceptor records four more. Each is here
-- because a document names it and 1305 has nowhere to put it — not because it seemed
-- useful:
--
--   event_id    D6' §2.2's envelope and `MageRide.Shared.Messaging.AuditEvent.EventId`
--               open with it, `admin-bff.yaml#AuditEvent` makes it a required Ulid, and
--               D6' §2.3 has consumers key on it for idempotency. The BIGINT identity is
--               an apply-order handle inside one database and cannot be any of those.
--   actor_role  `admin-bff.yaml#AuditEvent.actorRole`. Recorded rather than joined:
--               `iam.user_roles` is mutable and an auditor asking "who could do this in
--               March" must get March's answer, not today's. A role revoked after the
--               fact would otherwise erase the only record that it was ever held.
--   ip          The D-35 deliverable ("actor, action, target, before/after, ip") and
--               `admin-bff.yaml#AuditEvent.ip`. TEXT and not INET on purpose — the
--               address arrives from an X-Forwarded-For the C008 gateway sets, and an
--               audit trail that REFUSES a value it does not recognise records less than
--               one that writes down what it was handed.
--   detail      `admin-bff.yaml#AuditEvent.detail`. The interceptor's knowledge of the
--               request (method, path, status, idempotency key) kept apart from
--               `before`/`after`, which are the handler's knowledge of the entity. One
--               column holding both would make "what changed" unreadable.
--
-- Append-only is unchanged: this adds columns, no UPDATE path and no DELETE path.
-- Raised as a micro-change-set against server_db_schema.md §15 in the C062 handoff.
-- =====================================================================================

ALTER TABLE audit.events
  ADD COLUMN IF NOT EXISTS event_id UUID NOT NULL DEFAULT gen_random_uuid(),
  ADD COLUMN IF NOT EXISTS actor_role TEXT,
  ADD COLUMN IF NOT EXISTS ip TEXT,
  ADD COLUMN IF NOT EXISTS detail JSONB;

-- Unique, because D6' §2.3's consumers dedupe on it: two rows sharing an eventId would
-- make one of them invisible to every downstream sink. Not the primary key — the BIGINT
-- identity already is, and repointing it would rewrite the two indexes 1305 built.
CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_event_id ON audit.events(event_id);

COMMENT ON COLUMN audit.events.event_id IS
  'D6'' §2.2 envelope id and admin-bff.yaml#AuditEvent.eventId. Consumers key on it (D6'' §2.3); the BIGINT id is an apply-order handle and is not it.';
COMMENT ON COLUMN audit.events.actor_role IS
  'The canonical role the actor exercised AT THE TIME (AL-06). Recorded, never joined: iam.user_roles is mutable and a revoked grant would otherwise erase the record that it was held.';
COMMENT ON COLUMN audit.events.ip IS
  'Caller address as the C008 gateway reported it (D-35). TEXT rather than INET — an audit row must be able to record an address it cannot parse.';
COMMENT ON COLUMN audit.events.detail IS
  'What the admin-bff interceptor knows about the REQUEST (method, path, status). before/after are what the handler knows about the ENTITY; keeping them apart is what makes "what changed" readable.';
