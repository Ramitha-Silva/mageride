-- =====================================================================================
-- 0504 — trips: session end reasons, the idle clock, the destination memory and one rating
-- Source: D3' trip-state-svc · backend/contracts/trip-state.yaml · ADD §6 `trip-state-svc`
--         AL-32 · US-3.22/3.23 · US-5.3/5.4/5.9/5.10/5.12 · US-18.1/18.2 · R-15, T-04
--
-- ⚠ Spec gaps — micro-change-sets, raised in the C031 handoff.
-- =====================================================================================

-- --- 1. end_reason: the DDL and the API contract name different things -----------------
-- server_db_schema.md §4 and D4' §4 print
--     end_reason IN ('driver_ended','idle_timeout','geofence','admin')
-- and `backend/contracts/trip-state.yaml` (the machine-checkable form of D3') prints
--     endReason: [driver_ended, idle_timeout, destination_geofence, mqtt_offline]
--
-- Two of four agree. `geofence` and `destination_geofence` are one reason under two names, and
-- each document has one value the other lacks: the DDL has no way to record R-15/T-04's
-- last-will end at all, and the contract has no `admin`, which is what a support force-end is.
-- A service storing one vocabulary and serving the other would need a mapping table nobody
-- would remember to extend.
--
-- Resolved toward the **contract**, because that is the vocabulary a client branches on and
-- D3' §0 makes it part of the public API — plus `admin`, which is real and only the DDL has,
-- plus `ignition_off`, which AL-32/US-3.23 require and neither document has (see 3 below).
-- **server_db_schema.md §4 and D4' §4 should rename `geofence` → `destination_geofence` and
-- add `mqtt_offline` and `ignition_off`.**
--
-- Safe to replace outright: nothing has written this table — trip-state-svc is C031 and is the
-- only writer.
ALTER TABLE trips.sessions DROP CONSTRAINT IF EXISTS ck_sessions_end_reason;

ALTER TABLE trips.sessions
  ADD CONSTRAINT ck_sessions_end_reason CHECK (
    end_reason IS NULL
    OR end_reason IN ('driver_ended', 'idle_timeout', 'destination_geofence', 'mqtt_offline',
                      'ignition_off', 'admin'));

COMMENT ON COLUMN trips.sessions.end_reason IS
  'Why the session closed. driver_ended is the dashboard End Journey (US-5.2); idle_timeout is the 30-minute no-movement sweep (US-5.3); destination_geofence is the 100 m arrival (US-5.4); mqtt_offline is the EMQX last will (R-15, T-04); ignition_off is ACC off on a tracker-equipped vehicle (US-3.22/3.23); admin is a support force-end. Anything but driver_ended is auto-ended and opens the 5-minute restart grace (US-5.10).';

-- --- 2. The idle clock (US-5.3) --------------------------------------------------------
-- ⚠ "auto-ends it after 30 minutes of idle (no movement detected)" needs a *last moved*
--   instant per session, and no column in either spec holds one. Deriving it from
--   `telemetry.positions` would put a hypertable scan on a sweep that runs every minute, and
--   deriving it from `trips.position_samples` would only work for sessions whose samples that
--   table happens to carry.
--
--   Written by trip-state-svc's `telemetry.normalized` consumer, which is the one place that
--   sees every fix for a live session. `started_at` seeds it, so a session that never reports
--   a position still ages out rather than living forever.
ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS last_movement_at TIMESTAMPTZ;

ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS last_position_geo GEOGRAPHY(POINT, 4326);

ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS last_position_at TIMESTAMPTZ;

COMMENT ON COLUMN trips.sessions.last_movement_at IS
  'When the vehicle was last seen MOVING. The US-5.3 idle sweep measures from here; seeded to started_at so a session that never reports still ages out. Deliberately not the same as last_position_at — a bus parked at a terminus keeps reporting fixes, and treating those as activity would make the timer unreachable.';
COMMENT ON COLUMN trips.sessions.last_position_geo IS
  'The most recent fix on this session. Evaluated against destination_geo for US-5.4, and copied into end_geo when the session closes.';
COMMENT ON COLUMN trips.sessions.last_position_at IS
  'When last_position_geo was captured (the GNSS instant, not the receive time).';

-- The sweep's read: ACTIVE sessions whose last movement is older than the idle window. The
-- partial index keeps it proportional to the live fleet rather than to session history.
CREATE INDEX IF NOT EXISTS ix_sessions_idle_sweep
  ON trips.sessions(last_movement_at) WHERE state = 'ACTIVE';

-- The geofence sweep's read is an ST_DWithin over the armed subset, which is a small fraction of
-- even the live fleet — most journeys never arm one. A partial index over the predicate is what
-- keeps that scan off the whole table.
CREATE INDEX IF NOT EXISTS ix_sessions_geofence_armed
  ON trips.sessions(started_at)
  WHERE state = 'ACTIVE' AND auto_end_at_destination AND destination_geo IS NOT NULL;

-- --- 2b. The last-will clock (R-15, T-04) ----------------------------------------------
-- ⚠ D3' §3.2 routes `veh/{vehicleId}/status` to trip-state-svc and R-15/T-04 make it the signal
--   that a vehicle has gone away — and nothing says how long a coverage gap may last before the
--   journey is over. Ending on the first last will would close a session every time a bus passes
--   under a bridge, so the presence instant is recorded here and the sweep decides, after
--   `TripState:OfflineGrace`. A column rather than a Redis key because the decision must survive
--   a cache flush: a lost key would silently resurrect a session whose vehicle is long gone.
--   **D4' §4 should carry this column.**
ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS offline_since TIMESTAMPTZ;

COMMENT ON COLUMN trips.sessions.offline_since IS
  'When the broker last published this vehicle''s last will (R-15, T-04). Cleared when it comes back; the sweep ends the session once it has stayed away for TripState:OfflineGrace.';

CREATE INDEX IF NOT EXISTS ix_sessions_offline_sweep
  ON trips.sessions(offline_since) WHERE state = 'ACTIVE' AND offline_since IS NOT NULL;

-- --- 3. Who started it and who ended it (AL-32, US-5.12) -------------------------------
-- ⚠ AL-32 gives a tracker-equipped Mode A/B vehicle an auto-session on ignition **and** gives
--   the driver a dashboard Start/End that "overrides the device". Both write the same
--   transition, so the row cannot say which happened — and US-5.12's dashboard has to show
--   "journey started" for a device-started session while still offering End Journey. An
--   operator reading a support ticket cannot tell an ignition auto-start from a driver's tap.
--   **D4' §4 should carry both columns.**
ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS started_by TEXT NOT NULL DEFAULT 'driver';

ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS ended_by TEXT;

ALTER TABLE trips.sessions DROP CONSTRAINT IF EXISTS ck_sessions_started_by;
ALTER TABLE trips.sessions
  ADD CONSTRAINT ck_sessions_started_by CHECK (started_by IN ('driver', 'device', 'system'));

ALTER TABLE trips.sessions DROP CONSTRAINT IF EXISTS ck_sessions_ended_by;
ALTER TABLE trips.sessions
  ADD CONSTRAINT ck_sessions_ended_by CHECK (ended_by IS NULL OR ended_by IN ('driver', 'device', 'system'));

COMMENT ON COLUMN trips.sessions.started_by IS
  'driver = the dashboard Start Journey; device = ACC-on from a paired tracker (US-3.22/3.23); system = a grace restart. AL-32: the dashboard overrides the device, so both may write the same transition.';
COMMENT ON COLUMN trips.sessions.ended_by IS
  'Who closed it, beside end_reason: a driver_ended by `device` is ACC-off, by `driver` is the dashboard button.';

-- --- 4. Where the journey finished (US-5.4) --------------------------------------------
-- ⚠ `destination_geo` (0501) is the geofence a session ends *at*, and US-5.4 defines it as
--   "a 100 m radius of **the previous journey's end position**". Nothing records where a
--   journey ended, so the next one has no centre to arm the fence around. **D4' §4 should
--   carry this column** — without it US-5.4 is a rule with no input.
ALTER TABLE trips.sessions
  ADD COLUMN IF NOT EXISTS end_geo GEOGRAPHY(POINT, 4326);

COMMENT ON COLUMN trips.sessions.end_geo IS
  'Where this session ended, from the last position seen on it. Becomes the next session''s destination_geo when the driver arms auto-end-at-destination (US-5.4).';

-- The "previous journey's end position" read: newest ended session for a vehicle that has one.
CREATE INDEX IF NOT EXISTS ix_sessions_vehicle_ended
  ON trips.sessions(vehicle_id, ended_at DESC) WHERE end_geo IS NOT NULL;

-- --- 5. One rating per rater per session per direction (US-18.1/18.2) ------------------
-- ⚠ `trips.ratings` (0502) has no uniqueness, and the contract answers **409** to a second
--   rating ("One rating per passenger per session"). Without the index that rule is a race:
--   two taps on a flaky connection both read "no rating yet" and both insert. **D4' §4 should
--   carry this index.**
--
-- Keyed on direction as well, because a session is rated in both directions by two different
-- people and one of them may also be the other's ratee.
CREATE UNIQUE INDEX IF NOT EXISTS ux_ratings_once
  ON trips.ratings(subject_kind, subject_id, rater_id, direction);
