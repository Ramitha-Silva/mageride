-- =====================================================================================
-- 0309 — registry: transactional outbox
-- Source: D6' §2.1/§2.4 · D3' registry-svc route table · ADD §9.1 · D-22, E-09, R-13
--
-- ⚠ Spec gap — micro-change-set, raised in the C028 handoff, and the second half of the one
--   C021 left open ("C028 lands the table and the publish together").
--
--   D3' has `DELETE /v1/vehicles/{id}/share/{grantId}` "revoke → `share.revoked` (D-22)" and
--   D6' §5.2 has fanout-svc turn that event into a directed `RemoveFromGroupAsync` in under
--   200 ms. So the event has a named producer and a named consumer — and **no topic and no
--   outbox table**. D6' §2.1's registry lists six topics and none of them is registry-svc's;
--   D4' §2 and server_db_schema.md §2 have no `registry.outbox`.
--
--   Publishing straight to Redpanda instead would break the guarantee the outbox exists for:
--   a revoke that commits and then fails to publish leaves a passenger watching a vehicle they
--   no longer have access to, which is precisely the leak D-22 is about.
--
--   **D6' §2.1 should carry a `registry.events` topic** (partition key vehicleId, producer
--   registry-svc, consumers fanout · query · audit) **and D4' §2 this table.**
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- vehicleId. The Kafka partition key, so every event about one vehicle stays ordered — a
  -- `share.granted` overtaking its own `share.revoked` would restore visibility that was taken
  -- away (D6' §2.1 "default partition key = vehicleId").
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- share.revoked | vehicle.registered | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

-- Same claim shape as rides.outbox (0604): `WHERE dispatched_at IS NULL ORDER BY id LIMIT n
-- FOR UPDATE SKIP LOCKED`, so the partial index keeps the scan proportional to the backlog.
CREATE INDEX IF NOT EXISTS ix_registry_outbox_undispatched
  ON registry.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE registry.outbox IS
  'Transactional outbox for registry.events (D6'' §2.4, R-13). share.revoked is the D-22 event fanout-svc turns into a directed RemoveFromGroupAsync.';
COMMENT ON COLUMN registry.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

-- E-09: the NOTIFY is issued by MageRide.Shared's OutboxWriter inside the caller's transaction,
-- not by a trigger here — Postgres delivers a transactional NOTIFY at COMMIT, which is the
-- guarantee, and a trigger would fire for rows a later ROLLBACK discards. Same reasoning as 0604.
COMMENT ON SCHEMA registry IS
  'Vehicle, driver-profile and Mode B sharing identity. LISTEN/NOTIFY channel ''registry_outbox'' wakes the outbox dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
