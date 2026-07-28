-- =====================================================================================
-- 0403 — prov: transactional outbox
-- Source: D6' §2.1/§2.4 · D6' §4.3 · D3' provisioning-svc route table · E-09, R-13, T-03, T-12
--
-- ⚠ Spec gap — micro-change-set, raised in the C030 handoff; the same shape C028 raised for
--   `registry.events` (0309).
--
--   D3' `POST /v1/trackers/bind` lists "emit `tracker.bound`" as a side effect and D6' §4.3 has
--   the Redis IMEI cache "invalidated by `tracker.bound`/`tracker.unbound`". So both events have
--   a named producer and a named consumer — and **no topic and no outbox table**. D6' §2.1's
--   registry lists six topics, none of them provisioning-svc's, and neither D4' §3 nor
--   server_db_schema.md §3 has a `prov.outbox`.
--
--   Publishing straight to Redpanda instead would break exactly the guarantee T-12 needs: a
--   revoke that commits and then fails to publish leaves a decommissioned tracker publishing
--   positions until its 90-day certificate expires.
--
--   **D6' §2.1 should carry a `provisioning.events` topic** (partition key vehicleId, producer
--   provisioning-svc, consumers tcp-adapter · fanout · audit) **and D4' §3 this table.**
--
--   The Redis pub/sub channel D6' §4.2 names is a *second*, faster path for the same facts and
--   is not a substitute: it is fire-and-forget, so it carries the sub-second revocation signal
--   while this table carries the durable one.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS prov.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- vehicleId, matching `provisioning.events`' partition key. Keying by binding id instead
  -- would let a later `tracker.bound` for a re-bound IMEI overtake the `tracker.unbound` that
  -- released it, and a consumer would rebuild the cache entry it had just dropped.
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- tracker.bound | tracker.unbound | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

CREATE INDEX IF NOT EXISTS ix_prov_outbox_undispatched
  ON prov.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE prov.outbox IS
  'Transactional outbox for provisioning.events (D6'' §2.4, R-13). tracker.bound / tracker.unbound are the D6'' §4.3 cache-invalidation events; tracker.revoked is T-12.';
COMMENT ON COLUMN prov.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

COMMENT ON SCHEMA prov IS
  'Hardware tracker provisioning and credentials (§3, T-02/T-03/T-08). LISTEN/NOTIFY channel ''prov_outbox'' wakes the outbox dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
