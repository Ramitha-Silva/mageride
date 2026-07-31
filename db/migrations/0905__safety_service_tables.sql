-- =====================================================================================
-- 0905 — safety: the outbox, the replay log, and the four columns C005 could not know about
-- Source: D3' safety-svc · D5' §14.3 (SOS SLO) · server_db_schema.md §8 · ADD §9.1 ·
--         D-33, D-34, AL-13, AL-44, US-12.5, US-12.6, US-12.10, US-12.11
--
-- Owned by C052 (safety-svc). Everything here is a micro-change-set; each is argued at the
-- object rather than in a block, except the two that need more room:
--
--   (1) **The admin live feed has no transport.** D3' lists "admin live-feed WS" as a side
--       effect of POST /v1/sos, and `contracts/realtime/signalr-hub.md` has no admin group and
--       no SOS event — its §6 lists what is deliberately *not* on the hub and an SOS is not
--       among them, so the omission is a gap rather than a decision. CLAUDE.md's universal rule
--       settles the shape: cross-service state changes go through the transactional outbox, so
--       `safety.outbox` publishes `sos.raised` on `safety.events` and whoever draws SCR-AP-005
--       consumes it. The same micro-change-set C028/C030/C033/C044/C046 raised for their own
--       topics, and the ninth topic outside D6' §2.1's registry.
--
--   (2) **A vehicle report cannot name the driver it counts against.** §8's
--       `safety.vehicle_reports` has a reporter, a vehicle and an optional ride;
--       `reputation.v1.proto`'s `VehicleReport` requires a `driver_id`, and its own comment says
--       why — "the counter is the driver's, because `reputation.counters` is keyed by user". A
--       vehicle has an owner, not a driver, and the driver on the reported ride is the person
--       the passenger is complaining about. Resolved at report time and stored, because the ride
--       that identifies them is terminal by then and re-deriving it later would give a different
--       answer once the vehicle changed hands.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- safety.outbox — the admin live feed, and anything else that has to leave transactionally
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS safety.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- The person the event is about — the SOS raiser, the blocked driver, the reported vehicle's
  -- driver. Kafka partition key: two facts about one person must arrive in the order they
  -- happened, and there is no ride to key by (an SOS can be raised with no ride at all).
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- sos.raised | vehicle.reported | …
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_safety_outbox_undispatched
  ON safety.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE safety.outbox IS
  'Transactional outbox for safety.events (D6'' §2.4, R-13). Not in D4''/server_db_schema — see the header note and the C052 handoff. sos.raised is the admin live feed D3'' asks for and signalr-hub.md has no group for.';

-- -------------------------------------------------------------------------------------
-- safety.command_log — R-14 replay, the eleventh bounded context to need one
--
-- `safety.yaml` declares `Idempotency-Key` on POST /v1/sos, POST /v1/trip-share/{tripId},
-- POST /v1/reports/vehicle and POST /v1/drivers/{driverId}/block. Shape is 0803 exactly, minus
-- the aggregate-id column: an SOS targets no aggregate this service owns.
--
-- It matters most on the SOS. A double-tapped panic button under the same key must send one
-- SMS, not two — and the second tap is *likely*, because the first thing somebody does when
-- nothing appears to happen is press it again.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS safety.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_safety_command_log_inflight
  ON safety.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE safety.command_log IS
  'R-14 idempotent replay for safety-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';

-- -------------------------------------------------------------------------------------
-- safety.sos_events — when the alert actually went out
--
-- §8 gives the row a `ts` (when the button was pressed) and three free-text gateway columns,
-- and D3''s response is `{sosId, dispatchedAt}`. Those are two different instants, and the gap
-- between them *is* the D-33 SLO — measuring it needs both. `ts` alone would make every SOS look
-- instantaneous.
--
-- The three gateway columns C005 left as free text get their vocabulary here rather than a
-- CHECK: D6' §7.3 names two gateway families and a deployment may swap either, so constraining
-- the *values* would turn a configuration change into a migration. What is constrained is
-- `sms_status`, which is this service's own state and has three outcomes.
-- -------------------------------------------------------------------------------------
ALTER TABLE safety.sos_events
  ADD COLUMN IF NOT EXISTS dispatched_at TIMESTAMPTZ;

DO $$ BEGIN
  ALTER TABLE safety.sos_events ADD CONSTRAINT ck_sos_events_sms_status
    CHECK (sms_status IS NULL OR sms_status IN ('Dispatched','Failed','NoContact'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

COMMENT ON COLUMN safety.sos_events.dispatched_at IS
  'When a gateway accepted the alert. NULL while in flight or after both refused; `ts` is when the button was pressed, and the interval between them is the D-33 p99.';
COMMENT ON COLUMN safety.sos_events.sms_status IS
  'Dispatched (a gateway took it) | Failed (both refused) | NoContact (AL-13: nobody to send to). The gateway columns beside it record which transports were tried.';
COMMENT ON COLUMN safety.sos_events.primary_gateway IS
  'The primary transport this alert was handed to, and whether it answered first — D-33 sends to both in parallel and resolves on whichever delivers, so "tried" and "delivered" are different facts.';

-- -------------------------------------------------------------------------------------
-- safety.vehicle_reports — the driver the report counts against, and how it was resolved
--
-- `driver_id`: see header note (2).
-- The resolution columns: US-12.6's third confirmation is what auto-delists, so *when* and *by
-- whom* a report was confirmed is the evidence behind a delisting somebody will appeal. §8 has
-- `status` and no way to say who moved it.
-- -------------------------------------------------------------------------------------
ALTER TABLE safety.vehicle_reports
  ADD COLUMN IF NOT EXISTS driver_id UUID REFERENCES iam.users(id),
  ADD COLUMN IF NOT EXISTS resolved_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS resolved_by UUID REFERENCES iam.users(id),
  ADD COLUMN IF NOT EXISTS resolution_note TEXT;

DO $$ BEGIN
  ALTER TABLE safety.vehicle_reports ADD CONSTRAINT ck_vehicle_reports_resolution
    CHECK ((status = 'PENDING') = (resolved_at IS NULL));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- The US-12.6 tally is "CONFIRMED reports against this vehicle"; ix_vreports_vehicle covers
-- every report, so the count scans dismissed ones too. Partial on the status that decides.
CREATE INDEX IF NOT EXISTS ix_vreports_confirmed
  ON safety.vehicle_reports(vehicle_id) WHERE status = 'CONFIRMED';

-- One passenger, one report per ride: a passenger who taps Report twice on the same trip has
-- one complaint, and three taps must not be the three confirmations that delist a vehicle.
-- Partial, because `ride_id` is optional — a report with no ride has no natural key and the
-- command log is what dedupes it.
CREATE UNIQUE INDEX IF NOT EXISTS ux_vreports_reporter_ride
  ON safety.vehicle_reports(reporter_id, ride_id) WHERE ride_id IS NOT NULL;

COMMENT ON COLUMN safety.vehicle_reports.driver_id IS
  'Who the report counts against (reputation.v1.proto VehicleReport.driver_id). Resolved from the reported ride at report time — a vehicle has an owner, not a driver, and re-deriving it later would answer differently once the vehicle changed hands.';

-- -------------------------------------------------------------------------------------
-- safety.trip_share_tokens — issuing a D-34 link needs a row this table cannot find
--
-- 0901's ix_trip_share_tokens_trip is `(trip_id) WHERE revoked_at IS NULL`, which answers
-- "every live token for this trip" — the revocation query. Issuing asks a narrower question:
-- "is there a live token for this trip *in this scope*", because a package delivery legitimately
-- carries a package_recipient token and a trip_view one at the same time, and re-issuing must
-- replay only its own.
-- -------------------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS ix_trip_share_tokens_trip_scope
  ON safety.trip_share_tokens(trip_id, scope) WHERE revoked_at IS NULL;
