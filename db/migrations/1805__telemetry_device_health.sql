-- =====================================================================================
-- 1805 — telemetry: per-device health, the fleet threshold alert, and this plane's outbox
-- Source: URD US-3.13 / US-3.16 · ADD §7.7.7 (fleet scoping) · D6' §3.1/§3.4 · T-04, T-10
--         D7' §4.2 fleet-health-svc (`Health__OfflinePct`, `Health__WindowMin`)
--
-- Landed by C044 (fleet-health-svc). Three tables and one function; every one of them is a
-- micro-change-set recorded in the C044 handoff, because **no DDL source prints any of it**:
--
--   (a) US-3.13 asks for "the count and percentage of trackers in states Online / Stale (no
--       ping > 5 min) / Offline (no ping > 30 min) / Decommissioned across my fleet". That is a
--       per-device question and `telemetry.fleet_health_5m` (1802) cannot answer it: a
--       continuous aggregate is bucketed by time and blind to which device contributed, so it
--       can say 90 of 100 vehicles reported in the last five minutes and can never say which
--       ten did not, nor tell a 6-minute silence from a 6-hour one. `telemetry.device_health` is
--       the rollup that can, and it is the only new state fleet-health-svc keeps.
--
--   (b) US-3.16's "N % of my fleet goes offline within a 5-minute window" has to be raised
--       **once** per window however many replicas notice it. `ux_fleet_health_alert_window` is
--       that guarantee — the DoD's "exactly one alert per window" is an index, not a lock.
--
--   (c) The alert is delivered to notification-svc (C051) over the event backbone, so it needs
--       a transactional outbox (D6' §2.4, R-13) — the same shape and the same argument as
--       `registry.outbox` (0309) and `prov.outbox` (0403). Publishing straight to Redpanda
--       would let an alert commit and then vanish, which is the one delivery an operator
--       notices the absence of.
--
-- WHAT THIS FILE DELIBERATELY DOES NOT ADD
--   No second copy of `last_seen_at`. C030's own CLAUDE.md hands the four diagnostics columns
--   on `prov.tracker_bindings` — `last_seen_at`, `signal_strength`, `battery_mv`, `sat_count` —
--   to this service ("the columns are read here and written there"), so fleet-health-svc syncs
--   them from `device_health` on its sweep rather than provisioning-svc growing a second
--   writer. `device_health` is keyed by **vehicle**, matching the telemetry plane's key
--   everywhere else; `tracker_bindings` is keyed by IMEI, which is the credential's identity
--   and moves between vehicles.
-- =====================================================================================

-- --- The state ladder, as one expression ---------------------------------------------
-- Both the dashboard read and the alerting sweep classify a device, and they must never
-- disagree — the sweep is what decides that a state *changed* and the read is what an operator
-- sees, so a second implementation would show a fleet one thing and alert on another. It lives
-- in SQL rather than in C# because the read is a set-based query over a whole fleet.
--
-- IMMUTABLE and not STABLE: `at` is a parameter. Passing the clock in is what makes the
-- classification a pure function of the row, which is also what makes it unit-testable and
-- indexable if it ever needs to be.
CREATE OR REPLACE FUNCTION telemetry.device_health_state(
  binding_state     TEXT,
  decommissioned_at TIMESTAMPTZ,
  last_ping_at      TIMESTAMPTZ,
  last_status       TEXT,
  last_status_at    TIMESTAMPTZ,
  stale_after       INTERVAL,
  offline_after     INTERVAL,
  at                TIMESTAMPTZ)
RETURNS TEXT
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    -- US-3.8: a decommissioned tracker's credentials are revoked and no further ingest is
    -- possible. QUARANTINED is deliberately NOT here — T-08 holds a binding pending an admin
    -- decision (US-3.4) and it may well come back, so it reads as a device that is not
    -- reporting rather than one that has been retired.
    WHEN decommissioned_at IS NOT NULL OR binding_state = 'REVOKED' THEN 'DECOMMISSIONED'

    -- A tracker that has never reported is Offline, not Online. A bound device with no ping is
    -- exactly the case an operator opens this dashboard to find.
    WHEN last_ping_at IS NULL                        THEN 'OFFLINE'
    WHEN at - last_ping_at > offline_after           THEN 'OFFLINE'
    WHEN at - last_ping_at > stale_after             THEN 'STALE'

    -- The EMQX last will (R-15, T-04). The broker has said this device's session is gone, so it
    -- cannot be Online however recent its last ping was — but it is not Offline either, because
    -- US-3.13 defines Offline as 30 minutes of silence and a tunnel is not an outage. A fresher
    -- ping clears it with no `online` message needed, which matters because a device that
    -- crashed and restarted may never send one (the same rule C041 applies to `veh:offline`).
    WHEN last_status = 'offline' AND last_status_at > last_ping_at THEN 'STALE'

    ELSE 'ONLINE'
  END
$$;

COMMENT ON FUNCTION telemetry.device_health_state(TEXT, TIMESTAMPTZ, TIMESTAMPTZ, TEXT, TIMESTAMPTZ, INTERVAL, INTERVAL, TIMESTAMPTZ) IS
  'US-3.13''s four tracker states from one device_health row: Online | Stale | Offline | Decommissioned. Called by both the fleet dashboard read and fleet-health-svc''s transition sweep so the two cannot disagree. Thresholds are parameters (Health:StaleAfter / Health:OfflineAfter), not literals.';

-- --- Per-device health rollup --------------------------------------------------------
CREATE TABLE IF NOT EXISTS telemetry.device_health (
  -- The vehicle, not the binding. Every key on the telemetry plane is a vehicle id because
  -- EMQX authenticates a vehicle (mqtt-topics.md §1), and a tracker moved between vehicles
  -- must not carry the old vehicle's health with it. No FK to registry.vehicles: this is
  -- written by a stream consumer that must not block on another context's row, exactly as
  -- telemetry.positions (1801) is.
  vehicle_id        UUID PRIMARY KEY,

  -- Denormalised from the sample (mqtt-topics.md §6 — C040 populates `fleetId`), so a
  -- fleet-scoped read needs no join. NULL = the vehicle belongs to no fleet, and those rows are
  -- invisible to every fleet reader (device_health_fleet, below).
  fleet_id          UUID,
  imei              TEXT,                             -- from provisioning.events; NULL until a bind is seen

  -- Liveness. `last_ping_at` is the PLATFORM receive clock (the sample's receivedTs), not the
  -- GNSS instant: US-3.13 counts silence, and a tracker with a wrong clock would otherwise be
  -- permanently Stale or permanently Online. `last_sample_ts` keeps the GNSS instant for the
  -- record.
  last_ping_at      TIMESTAMPTZ,
  last_sample_ts    TIMESTAMPTZ,
  ping_source       SMALLINT,                         -- telemetry.positions.source domain, 0..4

  -- The retained `veh/{vehicleId}/status` payload (D6' §3.1/§3.4, T-04).
  last_status       TEXT,
  last_status_at    TIMESTAMPTZ,

  -- US-3.12's per-tracker diagnostics, as `sys/diag/{vehicleId}` reported them (D6' §3.1).
  -- Both battery columns exist because the device population reports two different things: a
  -- GT06 status byte carries a coarse voltage LEVEL, JT/T 808 additional items carry millivolts,
  -- and only a firmware that computes it can give a percentage. Storing a made-up percentage
  -- from a voltage would put a number on an operator's screen that no device said.
  signal_strength   SMALLINT,
  battery_mv        INTEGER,
  battery_pct       SMALLINT,
  sat_count         SMALLINT,
  last_diag_at      TIMESTAMPTZ,

  -- Mirrors prov.tracker_bindings.state as this service last heard it on provisioning.events.
  -- A mirror rather than a join: the dashboard read is one indexed scan over a fleet and a join
  -- to another schema on every row is the thing the 200 ms p95 budget cannot afford.
  binding_state     TEXT NOT NULL DEFAULT 'ACTIVE',
  decommissioned_at TIMESTAMPTZ,

  -- The last state the transition sweep recorded. NOT the answer the dashboard gives — that is
  -- derived fresh by device_health_state() so it is correct between sweeps and correct even
  -- with the sweep switched off. This column exists so a *change* is detectable, which is what
  -- an alert is raised on, and so `since` has something to report.
  observed_state    TEXT NOT NULL DEFAULT 'OFFLINE',
  state_changed_at  TIMESTAMPTZ NOT NULL DEFAULT now(),

  created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),

  CONSTRAINT ck_device_health_status CHECK (last_status IS NULL OR last_status IN ('online', 'offline')),
  CONSTRAINT ck_device_health_binding CHECK (binding_state IN ('ACTIVE', 'QUARANTINED', 'REVOKED')),
  CONSTRAINT ck_device_health_state
    CHECK (observed_state IN ('ONLINE', 'STALE', 'OFFLINE', 'DECOMMISSIONED')),
  CONSTRAINT ck_device_health_source CHECK (ping_source IS NULL OR ping_source BETWEEN 0 AND 4),
  CONSTRAINT ck_device_health_battery_pct
    CHECK (battery_pct IS NULL OR battery_pct BETWEEN 0 AND 100)
);

-- The fleet dashboard's only read (US-3.13). Partial, because most vehicles on the platform are
-- Mode C and belong to no fleet.
CREATE INDEX IF NOT EXISTS ix_device_health_fleet
  ON telemetry.device_health (fleet_id) WHERE fleet_id IS NOT NULL;

-- The sweep's ordering, and the "which devices have gone quiet" scan.
CREATE INDEX IF NOT EXISTS ix_device_health_ping
  ON telemetry.device_health (last_ping_at NULLS FIRST);

-- The IMEI-keyed sync back into prov.tracker_bindings (US-3.12's writer).
CREATE INDEX IF NOT EXISTS ix_device_health_imei
  ON telemetry.device_health (imei) WHERE imei IS NOT NULL;

SELECT public.attach_set_updated_at('telemetry', 'device_health');

COMMENT ON TABLE telemetry.device_health IS
  'Per-device tracker health, one row per vehicle (US-3.13, T-04). Written only by fleet-health-svc (C044) from telemetry.normalized, veh/{vehicleId}/status, sys/diag/{vehicleId} and provisioning.events. The four states are DERIVED by telemetry.device_health_state() at read time; observed_state is the sweep''s record of the last transition, not the answer.';
COMMENT ON COLUMN telemetry.device_health.last_ping_at IS
  'Platform receive clock of the newest accepted sample. US-3.13''s "no ping > 5 min / > 30 min" is measured from here, so a tracker with a wrong GNSS clock cannot pin itself Online or Stale.';
COMMENT ON COLUMN telemetry.device_health.observed_state IS
  'The state the last transition sweep saw. Compared against a freshly derived state to detect a flip; never returned to a caller.';
COMMENT ON COLUMN telemetry.device_health.binding_state IS
  'Mirror of prov.tracker_bindings.state from provisioning.events. Denormalised so the fleet dashboard is one indexed scan and not a cross-schema join per row.';

-- --- The fleet threshold alert (US-3.16, D7' §4.2 Health__OfflinePct / Health__WindowMin) ---
CREATE TABLE IF NOT EXISTS telemetry.fleet_health_alerts (
  id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id           UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  -- The window this alert is about — the `time_bucket` start of the closed
  -- telemetry.fleet_health_5m bucket it was computed from, so an alert and the rollup row
  -- behind it are joinable.
  bucket             TIMESTAMPTZ NOT NULL,
  window_minutes     SMALLINT NOT NULL,
  expected_vehicles  INTEGER NOT NULL,                -- ACTIVE tracker bindings on the fleet
  reporting_vehicles INTEGER NOT NULL,                -- fleet_health_5m.active_vehicles for the bucket
  offline_vehicles   INTEGER NOT NULL,
  offline_pct        NUMERIC(5, 2) NOT NULL,
  threshold_pct      NUMERIC(5, 2) NOT NULL,          -- what Health:OfflinePct was when it fired
  raised_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

  CONSTRAINT ck_fleet_health_alert_counts CHECK (
    expected_vehicles > 0
    AND reporting_vehicles >= 0
    AND offline_vehicles >= 0
    AND offline_vehicles <= expected_vehicles),
  CONSTRAINT ck_fleet_health_alert_window CHECK (window_minutes > 0),
  CONSTRAINT ck_fleet_health_alert_pct CHECK (offline_pct >= 0 AND threshold_pct >= 0)
);

-- The DoD's "exactly one alert per window". Every replica evaluates every window, so the
-- guarantee has to be in the database: the INSERT is the claim, and a replica whose insert
-- returns no row writes no outbox event either.
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_health_alert_window
  ON telemetry.fleet_health_alerts (fleet_id, bucket);

-- "What has this fleet been alerted about lately" — the Fleet Portal's read.
CREATE INDEX IF NOT EXISTS ix_fleet_health_alert_recent
  ON telemetry.fleet_health_alerts (fleet_id, bucket DESC);

COMMENT ON TABLE telemetry.fleet_health_alerts IS
  'Device-down threshold breaches (US-3.16): > Health:OfflinePct of a fleet''s ACTIVE trackers not reporting inside one Health:WindowMin window. ux_fleet_health_alert_window is what makes it one alert per window across every replica.';
COMMENT ON COLUMN telemetry.fleet_health_alerts.threshold_pct IS
  'The threshold in force when the alert fired. Stored rather than read back from configuration so an alert stays explicable after an operator retunes it.';

-- --- Transactional outbox for `fleet.events` (D6' §2.4, R-13, E-09) -------------------
CREATE TABLE IF NOT EXISTS telemetry.outbox (
  id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- fleetId, matching `fleet.events`' partition key. An alert is a fact about an organisation
  -- and two windows' alerts for one fleet must arrive in order.
  aggregate_id  UUID NOT NULL,
  event_type    TEXT NOT NULL,                        -- fleet.health_alert
  payload       JSONB NOT NULL,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_telemetry_outbox_undispatched
  ON telemetry.outbox (id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE telemetry.outbox IS
  'Transactional outbox for fleet.events (D6'' §2.4, R-13), written by fleet-health-svc. LISTEN/NOTIFY channel ''telemetry_outbox'' wakes the dispatcher (E-09); the NOTIFY is issued by the writing transaction, not by a table trigger.';
COMMENT ON COLUMN telemetry.outbox.dispatched_at IS
  'Delivery is at-least-once: the row is marked only after the broker acks, so consumers dedupe on eventId (D6'' §2.3).';

-- --- Fleet scoping (ADD §7.7.7, ADD §9.5 item 8) --------------------------------------
-- The 1804 convention: the fleet-scoped counterpart of a telemetry relation is its name +
-- `_fleet`, filtered by telemetry.current_fleet_id() and granted to mageride_fleet_reader
-- alone. ADD §7.7.7 names this service specifically — "fleet-health-svc and the Admin Portal
-- apply row-level security so an operator only sees their own organisation's devices" — and the
-- point of putting the predicate in the database is that the endpoint cannot forget it.
-- current_fleet_id() returns NULL when the GUC is unset, so an unscoped connection sees no
-- rows rather than every fleet's.
-- `created_at` / `updated_at` are deliberately not exposed: they are this service's bookkeeping,
-- not a fact about a device, and `migrate-verify.sh` asserts that every `updated_at` column in an
-- owned schema carries the §0.2 trigger — which a view cannot.
CREATE OR REPLACE VIEW telemetry.device_health_fleet WITH (security_barrier = true) AS
  SELECT vehicle_id, fleet_id, imei, last_ping_at, last_sample_ts, ping_source,
         last_status, last_status_at, signal_strength, battery_mv, battery_pct, sat_count,
         last_diag_at, binding_state, decommissioned_at, observed_state, state_changed_at
    FROM telemetry.device_health
   WHERE fleet_id IS NOT NULL
     AND fleet_id = telemetry.current_fleet_id();

CREATE OR REPLACE VIEW telemetry.fleet_health_alerts_fleet WITH (security_barrier = true) AS
  SELECT id, fleet_id, bucket, window_minutes, expected_vehicles, reporting_vehicles,
         offline_vehicles, offline_pct, threshold_pct, raised_at
    FROM telemetry.fleet_health_alerts
   WHERE fleet_id = telemetry.current_fleet_id();

COMMENT ON VIEW telemetry.device_health_fleet IS
  'Fleet-scoped per-device tracker health (US-3.13). Rows are filtered by the app.fleet_id GUC; a vehicle that belongs to no fleet is invisible to every fleet reader.';
COMMENT ON VIEW telemetry.fleet_health_alerts_fleet IS
  'Fleet-scoped view of telemetry.fleet_health_alerts, filtered by the app.fleet_id GUC.';

REVOKE ALL ON telemetry.device_health        FROM PUBLIC;
REVOKE ALL ON telemetry.fleet_health_alerts  FROM PUBLIC;
REVOKE ALL ON telemetry.outbox               FROM PUBLIC;

GRANT SELECT ON telemetry.device_health_fleet        TO mageride_fleet_reader;
GRANT SELECT ON telemetry.fleet_health_alerts_fleet  TO mageride_fleet_reader;
