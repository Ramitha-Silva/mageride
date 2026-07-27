-- =====================================================================================
-- 0709 — dispatch: transactional outbox
-- Source: D6' §2.1 (topic `dispatch.events`) + §2.4 · ADD §9.1 · R-13, E-09
--
-- ⚠ MICRO-CHANGE-SET. Neither server_db_schema.md §6 nor D4' §6 declares this table, yet
--   D6' §2.4 names dispatch-svc alongside ride-svc as an outbox writer — "ride-svc/dispatch-svc
--   write domain change + outbox row in one DB transaction; offer.created pushed only after
--   COMMIT (no phantom offers, R-13)" — and D6' §2.1 registers `dispatch.events` with
--   dispatch-svc as its producer. `offer.created` IS the event R-13 exists for, and it is
--   written here, not in rides.*.
--
--   Without this table dispatch-svc (C034) would have to publish outside its transaction,
--   which is exactly the phantom-offer failure R-13 forbids. Shape is identical to
--   rides.outbox (0604) so MageRide.Shared's OutboxWriter/OutboxDispatcher work unchanged
--   with Outbox__Schema=dispatch, Outbox__Channel=dispatch_outbox, Outbox__Topic=dispatch.events.
--
--   specs/D4_mageride_data_model.md §6 and specs/server_db_schema.md §6 need this DDL added.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  aggregate_id UUID NOT NULL,                                 -- rideId; the Kafka partition key
  event_type TEXT NOT NULL,                                   -- offer.created | offer.expired | directional.cleared | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_dispatch_outbox_undispatched
  ON dispatch.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE dispatch.outbox IS
  'Transactional outbox for dispatch.events (D6'' §2.4, R-13). Not in D4''/server_db_schema — see the header note and the C004 handoff.';
COMMENT ON SCHEMA dispatch IS
  'Candidate scoring, offers, Job Board, driver levels and Directional Travel. LISTEN/NOTIFY channel ''dispatch_outbox'' wakes the outbox dispatcher (E-09).';
