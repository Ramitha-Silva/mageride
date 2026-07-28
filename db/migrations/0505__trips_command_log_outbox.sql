-- =====================================================================================
-- 0505 — trips: idempotent command replay log and the transactional outbox
-- Source: D3' §0 "Idempotency" · D6' §2.1/§2.4 · ADD §11.13 · R-13, R-14, E-09
-- =====================================================================================

-- ⚠ Spec gap — micro-change-set, raised in the C031 handoff. The fourth instance of the one
--   C020 (iam 0104), C021 (registry 0307) and C030 (prov 0402) raised, so this is settled:
--   **D4' §5 should print one command-log table per service with idempotent POSTs**, not for
--   rides alone. D3' marks every trip-state mutation "Idempotency-Key"; pointing them at
--   `rides.command_log` would give two bounded contexts one primary key, so a session start
--   and a ride command could collide on an identical client-generated key.
--
--   Shape is 0307 exactly (0603 minus `ride_id`): a start targets a session that does not
--   exist yet, and MageRide.Shared's PostgresCommandLog omits the column when
--   CommandLog:AggregateIdColumn is null.
CREATE TABLE IF NOT EXISTS trips.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the driver, or the rating passenger
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_trips_command_log_inflight
  ON trips.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE trips.command_log IS
  'R-14 idempotent replay for trip-state-svc''s POST mutations (D3'' §0). A replayed start returns the original session rather than colliding with the active-session mutex it just took.';

-- ⚠ Second half of the same gap, and a narrower one. **The topic exists**: D6' §2.1 has
--   `trip.events` ("Mode A/B session transitions from trip-state-svc, key vehicleId"), so
--   unlike C028's `registry.events` and C030's `provisioning.events` nothing new is claimed
--   here. What is missing is the table on this side of it: neither D4' §4 nor
--   server_db_schema.md §4 has a `trips.outbox`, so the one producer D6' §2.1 names has no
--   transactional way to write to the topic it names.
--
--   Publishing straight to Redpanda instead would break the guarantee R-13 exists for: a
--   session end that commits and then fails to publish leaves fanout-svc showing a bus on the
--   passenger map after its journey finished, and the driver has no way to take it off.
--
--   **D4' §4 should carry this table.** Note `trips.events` (0502) is a *different* thing — a
--   domain-visible log of what happened on a session, not a delivery queue; the two are
--   deliberately separate and both are written in the same transaction.
CREATE TABLE IF NOT EXISTS trips.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- vehicleId, matching `trip.events`' partition key (D6' §2.1). Keying by session id would
  -- order events per session, and the ordering that matters is per vehicle: an end and the
  -- start that follows it must not be reordered, or a consumer rebuilds the live-map entry it
  -- has just removed.
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- session.started | session.ended | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

CREATE INDEX IF NOT EXISTS ix_trips_outbox_undispatched
  ON trips.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE trips.outbox IS
  'Transactional outbox for trip.events (D6'' §2.4, R-13). Distinct from trips.events (0502), which is the domain log rather than a delivery queue.';
COMMENT ON COLUMN trips.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

COMMENT ON SCHEMA trips IS
  'Mode A/B tracking sessions, their domain events and ratings (D-03, R-01). LISTEN/NOTIFY channel ''trips_outbox'' wakes the outbox dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
