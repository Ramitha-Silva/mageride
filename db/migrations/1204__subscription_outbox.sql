-- =====================================================================================
-- 1204 — subscription: transactional outbox
-- Source: D6' §2.1/§2.4 · D3' subscription-svc Epic 23 · D5' BR-23.11 · D-22, E-09, R-13
--
-- ⚠ Spec gap — micro-change-set, raised in the C048 handoff. The same one C028's 0309
--   records, arriving a second time from the other end of the same event.
--
--   BR-23.11 and `POST /v1/mode-b/subscriptions/{id}/unsubscribe` put the passenger's own
--   unsubscribe on **subscription-svc**, and require the revocation to reach the passenger's
--   socket in under 200 ms (D-22, D6' §5.2). The event that does that is `share.revoked`, and
--   D6' §2.1 gives subscription-svc no topic and D4' §18b no outbox table — exactly the gap
--   0309 opened for registry-svc.
--
--   Publishing straight to Redpanda instead would break the guarantee the outbox exists for:
--   an unsubscribe that commits and then fails to publish leaves the passenger still watching
--   the vehicle they just left, which is the leak D-22 is about. Publishing *before* the
--   commit is worse — it revokes a passenger whose unsubscribe then rolls back.
--
--   **D6' §2.1's `registry.events` should list subscription-svc as a second producer** (same
--   partition key, same two event types) **and D4' §18b should carry this table.** The topic is
--   deliberately not a new one: fanout-svc's consumer (C041) is keyed on the `eventType` header
--   and the vehicle id, so a second producer on the same partition key keeps every event about
--   one vehicle ordered — which a second topic could not.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS subscription.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- vehicleId, matching registry.outbox. The Kafka partition key, so an accept's
  -- `share.granted` can never overtake the `share.revoked` of the unsubscribe before it and
  -- restore visibility that was taken away (D6' §2.1 "default partition key = vehicleId").
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- share.granted | share.revoked
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

-- Same claim shape as registry.outbox (0309) and rides.outbox (0604): `WHERE dispatched_at IS
-- NULL ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED`, so the partial index keeps the scan
-- proportional to the backlog rather than to the history.
CREATE INDEX IF NOT EXISTS ix_subscription_outbox_undispatched
  ON subscription.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE subscription.outbox IS
  'Transactional outbox for the Epic 23 half of registry.events (D6'' §2.4, R-13). share.revoked is BR-23.11''s unsubscribe and share.granted the accept that starts a subscription; fanout-svc turns them into the D-22 directed group changes.';
COMMENT ON COLUMN subscription.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

-- E-09: the NOTIFY is issued by MageRide.Shared's OutboxWriter inside the caller's transaction,
-- not by a trigger here — Postgres delivers a transactional NOTIFY at COMMIT, which is the
-- guarantee, and a trigger would fire for rows a later ROLLBACK discards. Same reasoning as 0309.
COMMENT ON SCHEMA subscription IS
  'Mode B passenger subscriptions, requests and pass-through payments (§18b, Epic 23). LISTEN/NOTIFY channel ''subscription_outbox'' wakes the outbox dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
