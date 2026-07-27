-- =====================================================================================
-- 0604 — rides: transactional outbox
-- Source: server_db_schema.md §5 · D4' §5 · D6' §2.1/§2.4 · ADD §9.1 · R-13, E-09
--
-- ride-svc writes the aggregate change and the outbox row in ONE transaction; the dispatcher
-- publishes to Redpanda topic `ride.events` keyed by rideId only after COMMIT — that ordering
-- is what R-13 ("no phantom offers") means.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS rides.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  aggregate_id UUID NOT NULL,                                 -- rideId; the Kafka partition key
  event_type TEXT NOT NULL,                                   -- ride.requested | ride.accepted | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

-- The drain claims `WHERE dispatched_at IS NULL ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED`;
-- the partial index keeps that scan proportional to the backlog, not to the table.
CREATE INDEX IF NOT EXISTS ix_outbox_undispatched
  ON rides.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE rides.outbox IS
  'Transactional outbox for ride.events (D6'' §2.4, R-13). Monthly range-partition candidate as volume grows (§21).';
COMMENT ON COLUMN rides.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

-- E-09: the sub-50 ms wake-up. The NOTIFY is issued by MageRide.Shared''s OutboxWriter inside
-- the caller's transaction rather than by a trigger here — Postgres delivers a transactional
-- NOTIFY at COMMIT, which is precisely the R-13 guarantee, and a trigger would fire for rows
-- that a later ROLLBACK discards. Recorded so nobody adds the trigger the spec comment hints
-- at (server_db_schema §5: "A NOTIFY on this table's INSERT ... wakes the dispatcher").
COMMENT ON SCHEMA rides IS
  'Mode C ride aggregate (R-01). LISTEN/NOTIFY channel ''ride_outbox'' wakes the outbox dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
