-- =====================================================================================
-- 0803 — reputation: intake ledger, transactional outbox, command log
-- Source: D3' reputation-svc (D-04, E-07) · D6' §2.1/§2.4 · D3' §0 "Idempotency" · R-13, R-14, E-09
--
-- ⚠ MICRO-CHANGE-SET (three of them, all raised in the C033 handoff). Neither
--   server_db_schema.md §7 nor D4' §7 declares any of these; each is required by a rule that
--   is spec'd elsewhere.
--
--   (a) `reputation.intake_log`. reputation-svc counts the same fact from two directions —
--       the D3' `ReportCancellation`/`ReportNoShow`/`ReportVehicle` gRPC calls, and the
--       `ride.events` topic D6' §2.1 lists it as a consumer of. D6' §2.3 makes delivery
--       at-least-once and says consumers "key on eventId"; a gRPC retry has the same shape.
--       Counting a redelivered driver-cancel twice would booking-disable a passenger in two
--       rides rather than three (D5' §7.2), so the counters need an intake ledger, not a
--       convention. It is a ledger and not just a dedupe key because "why is this counter 2?"
--       is the first question an appeal asks.
--
--   (b) `reputation.outbox`. E-07's `fraud.suspected` has a producer (this service) and a
--       consumer (the admin fraud-review queue, ADD §12.6) and no topic anywhere. Publishing
--       it outside a transaction would let a flag exist with no event, or an event naming a
--       flag that rolled back — the phantom R-13 forbids. Shape is `dispatch.outbox` (0709)
--       exactly, so MageRide.Shared's OutboxWriter/OutboxDispatcher work unchanged with
--       Outbox__Schema=reputation, Outbox__Channel=reputation_outbox,
--       Outbox__Topic=reputation.events.
--
--   (c) `reputation.command_log`. The fourth per-service command log (iam 0104, registry 0307,
--       dispatch 0710) and the same argument: D3' §0 requires `Idempotency-Key` on every POST
--       mutation and replays from a **per-service** log, and `reputation.yaml` declares the
--       header on the admin routes. Sharing another context's log would let an admin's key
--       collide with a passenger's booking key.
-- =====================================================================================

-- Every counted fact, once. The primary key IS the idempotency mechanism: intake is a single
-- INSERT ... ON CONFLICT (dedupe_key) DO NOTHING, so a redelivered event and a retried gRPC
-- call are settled by the index rather than by application locking.
CREATE TABLE IF NOT EXISTS reputation.intake_log (
  -- '{source}:{eventId}' for a topic message, '{kind}:{rideId}:{subjectId}' for a gRPC report
  -- whose caller minted no event id. Deliberately TEXT and not UUID: the two namespaces have to
  -- coexist, and a caller that can only name the ride still gets exactly-once counting.
  dedupe_key TEXT PRIMARY KEY,
  kind TEXT NOT NULL,                                         -- cancellation | no_show | report | completion
  subject_id UUID NOT NULL REFERENCES iam.users(id),          -- whose counter moved
  subject_role TEXT NOT NULL
    CONSTRAINT ck_intake_log_role CHECK (subject_role IN ('passenger','driver')),
  ride_id UUID,                                               -- no FK: outlives the ride row it came from
  source TEXT NOT NULL
    CONSTRAINT ck_intake_log_source CHECK (source IN ('grpc','ride.events','admin')),
  detail JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_intake_log_subject
  ON reputation.intake_log(subject_id, ts DESC);
-- The E-07 pair detector reads (subject, ride) pairs over a 30-day window; without this the
-- window scan is a seq scan of every fact the service has ever counted.
CREATE INDEX IF NOT EXISTS ix_intake_log_kind_ts
  ON reputation.intake_log(kind, ts DESC);

COMMENT ON TABLE reputation.intake_log IS
  'Exactly-once ledger for every counted fact (D6'' §2.3 at-least-once delivery). One row per counter movement; the counters themselves are derived from these and are the read model.';

CREATE TABLE IF NOT EXISTS reputation.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  aggregate_id UUID NOT NULL,                                 -- the flagged/blocked user; Kafka partition key
  event_type TEXT NOT NULL,                                   -- fraud.suspected | reputation.block_state_changed
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_reputation_outbox_undispatched
  ON reputation.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE reputation.outbox IS
  'Transactional outbox for reputation.events (D6'' §2.4, R-13). Not in D4''/server_db_schema — see the header note and the C033 handoff. Keyed by userId, not rideId: a block state is a fact about a person, and ordering must hold per person.';

CREATE TABLE IF NOT EXISTS reputation.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the deciding admin, or NULL for an internal caller
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte (R-14)
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_reputation_command_log_inflight
  ON reputation.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE reputation.command_log IS
  'R-14 idempotent replay for reputation-svc''s admin POSTs (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';

COMMENT ON SCHEMA reputation IS
  'Cancellation / no-show / report counters, the effective block state dispatch-svc gates on over gRPC (D-04), and the E-07 anti-collusion detector. LISTEN/NOTIFY channel ''reputation_outbox'' wakes the outbox dispatcher (E-09).';
