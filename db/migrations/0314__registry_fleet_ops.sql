-- =====================================================================================
-- 0314 — registry: time-bounded driver assignment, fleet scheduling and bulk vehicle
--        onboarding jobs
-- Source: backend/contracts/fleet.yaml assignDriverToVehicle / createFleetSchedule /
--         bulkAddFleetVehicles · D3' "fleet-svc (Phase 1, AL-03)"
--         · US-13.1, US-13.2, US-13.8, US-13.9, US-13.11 · AL-03, AL-23, AL-50
--         · server_db_schema.md §2
--
-- C059 (fleet-svc-fleet-ops). Three changes; all three are spec gaps raised in the C059
-- handoff, and the first was named as C059's to close by migration 0310's own header.
--
-- (a) ⚠ AN ASSIGNMENT CANNOT EXPIRE — micro-change-set, and the one 0310 left open.
--     US-13.9 says an assignment "**auto-expires**" and AL-23 makes time-bounding the whole
--     mechanism by which a Mode A/B driver is *temporarily* hired. `fleet.yaml` types the
--     request body `{driverId, vehicleId, from, to?}` and the `Assignment` schema returns
--     both. `registry.fleet_assignments` (0306) carries `assigned_at` and `revoked_at` and
--     nothing else, so the only way an assignment could end was a human revoking it — which
--     is precisely "manual action". 0310's header records the gap and names this component:
--     "**C059 owns assignment writes and should add `expires_at`**, after which the WHERE
--     clause below gains one predicate and nothing else changes." That is what happens here.
--     **server_db_schema.md §2 / D4' §2 should carry `valid_from` and `expires_at`.**
--
-- (b) ⚠ THERE IS NOWHERE TO PUT A FLEET SCHEDULE — micro-change-set.
--     US-13.11 gives the operator per-vehicle scheduled rides with a not-started alarm that
--     rings in the assigned driver's app, and `fleet.yaml` specifies
--     `POST /v1/fleets/{fleetId}/schedules` completely — `{vehicleId, routeId?, departAt,
--     notStartedAlarmMinutes}` in, a `FleetSchedule` out. No table exists anywhere.
--     `dispatch.scheduled_rides` (0704) is **not** it and must not be reused: it is a
--     passenger's Mode C advance booking (`passenger_id NOT NULL`, `pickup_geo`,
--     `dropoff_geo`, a `rides.rides` id) and AL-03 forbids a fleet Mode C vehicle outright.
--     A bus leaving the depot at 06:10 has no passenger and no pickup point.
--     **server_db_schema.md §6 / D4' §6 should carry `registry.fleet_schedules`.**
--
-- (c) ⚠ NO TABLE FOR A BULK VEHICLE JOB — micro-change-set, the same gap 0405 raised for
--     bulk *trackers*. `fleet.yaml` answers `POST /v1/fleets/{fleetId}/vehicles/bulk` with
--     `202 {jobId, totalRows, status, errorReportUrl}` and US-13.1 asks for the Epic 3
--     "downloadable error report"; a job held in memory cannot answer a poll after a
--     restart and cannot render a report at all. Shaped exactly like `prov.bulk_jobs` /
--     `prov.bulk_job_rows`, for the reason recorded there — the report is per row and a
--     JSONB document would be rewritten once per row.
--     **D4' §2 should carry both tables.**
--
-- The fourth change C059 needs is `spatial.geofences.fleet_id`, and it is **1408**: this
-- range runs long before `spatial` exists, which is also why `fleet_schedules.route_id`
-- lands here without its foreign key. Row-level security, the grants and the `_fleet` views
-- for everything added here are **1807** — it depends on `mageride_fleet_reader` (1804),
-- which cannot be created from the registry range either.
-- =====================================================================================

-- btree_gist lets a GiST index carry the scalar columns an equality is written on, which is
-- what (a)'s exclusion constraint needs. Standard contrib, present in
-- timescale/timescaledb-ha:pg16 and in every PostGIS image this repo runs on; guarded all the
-- same, because an extension is a cluster-level object a restricted role may not create.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- -------------------------------------------------------------------------------------
-- (a) The assignment's validity window (US-13.9 "auto-expires", AL-23)
-- -------------------------------------------------------------------------------------

ALTER TABLE registry.fleet_assignments
  ADD COLUMN IF NOT EXISTS valid_from TIMESTAMPTZ NOT NULL DEFAULT now(),
  ADD COLUMN IF NOT EXISTS expires_at TIMESTAMPTZ;

-- `valid_from` is not `assigned_at` renamed, and the two are deliberately both kept.
-- `assigned_at` is when the row was written — an audit fact, and what an "assignment
-- history" screen (SCR-FP-005) orders by. `valid_from` is when the driver may start driving,
-- which US-13.9's temporary hire routinely puts in the future: a relief driver booked on
-- Monday for Thursday's shift must not be able to take the bus out on Monday.
COMMENT ON COLUMN registry.fleet_assignments.valid_from IS
  'When the assignment starts conferring the right to drive (fleet.yaml `from`). Distinct from assigned_at, which is when the row was written — a relief driver booked days ahead has a future valid_from (US-13.9, AL-23).';
COMMENT ON COLUMN registry.fleet_assignments.expires_at IS
  'When it stops, or NULL for open-ended (fleet.yaml `to`). US-13.9''s "auto-expires": registry.driver_eligible_vehicles stops returning the vehicle the moment this passes, with nobody revoking anything (C059 gap (a)).';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.fleet_assignments'::regclass
                    AND conname = 'ck_fleet_assign_window') THEN
    ALTER TABLE registry.fleet_assignments
      ADD CONSTRAINT ck_fleet_assign_window
      CHECK (expires_at IS NULL OR expires_at > valid_from);
  END IF;
END $$;

COMMENT ON CONSTRAINT ck_fleet_assign_window ON registry.fleet_assignments IS
  'An assignment that expires before it starts confers nothing and would sit in the roster looking live. Refused at the column rather than in one service''s validator.';

-- 0306's `ux_fleet_assign_active (vehicle_id, driver_id) WHERE revoked_at IS NULL` said "one
-- open assignment per (vehicle, driver)", which was right while an assignment had no end. It
-- is wrong now in the direction that matters: an expired-but-unrevoked row would permanently
-- block re-hiring the same relief driver on the same bus next month, and the operator's only
-- way out would be to revoke a row that had already lapsed — manual action, for an expiry that
-- is meant to need none.
--
-- The rule that actually holds is "no two OPEN assignments of one driver to one vehicle whose
-- validity windows overlap". That is an exclusion constraint, not a unique index, and it is
-- also the only form that survives two managers assigning at once — a SELECT-then-INSERT loses
-- that race.
DROP INDEX IF EXISTS registry.ux_fleet_assign_active;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'registry.fleet_assignments'::regclass
                    AND conname = 'ex_fleet_assign_overlap') THEN
    ALTER TABLE registry.fleet_assignments
      ADD CONSTRAINT ex_fleet_assign_overlap
      EXCLUDE USING gist (
        vehicle_id WITH =,
        driver_id WITH =,
        tstzrange(valid_from, expires_at) WITH &&)
      WHERE (revoked_at IS NULL);
  END IF;
END $$;

COMMENT ON CONSTRAINT ex_fleet_assign_overlap ON registry.fleet_assignments IS
  'A driver holds at most one open assignment to a given vehicle at any instant (fleet.yaml assignDriverToVehicle). Consecutive non-overlapping windows are legal and are how a relief driver is re-hired; replaces 0306''s ux_fleet_assign_active, which could not tell an expired row from a live one.';

-- The eligibility read is "which vehicles may this driver operate *now*", so it is keyed by
-- driver and bounded by the window. 0310's ix_fleet_assign_driver_active is partial on
-- `revoked_at IS NULL` and stays; this one carries the two window columns so the added
-- predicates are answered from the index rather than by refetching every unrevoked row.
CREATE INDEX IF NOT EXISTS ix_fleet_assign_window
  ON registry.fleet_assignments(driver_id, valid_from, expires_at) WHERE revoked_at IS NULL;

-- -------------------------------------------------------------------------------------
-- (a, continued) The projection every consumer reads, now honouring expiry
-- -------------------------------------------------------------------------------------

-- Identical to 0310 apart from the two predicates on the 'assigned' branch, which is what its
-- header promised: "the WHERE clause below gains one predicate and nothing else changes".
-- Recreated in full rather than patched, because CREATE OR REPLACE VIEW cannot change a
-- column list and a future reader must be able to see the whole definition in one place.
DROP VIEW IF EXISTS registry.driver_eligible_vehicles;

CREATE VIEW registry.driver_eligible_vehicles AS
SELECT DISTINCT ON (candidate.driver_id, candidate.vehicle_id)
       candidate.driver_id,
       candidate.vehicle_id,
       candidate.source,
       candidate.fleet_id,
       v.owner_id,
       v.registration_number,
       v.vehicle_type,
       v.mode,
       v.status,
       v.dispatch_state,
       v.onboarding_status,
       v.driver_name,
       v.driver_photo_url,
       v.created_at,
       (v.status = 'APPROVED' AND v.dispatch_state = 'ACTIVE') AS is_go_live_eligible
  FROM (
        SELECT owner_id AS driver_id,
               id       AS vehicle_id,
               'owned'  AS source,
               NULL::uuid AS fleet_id
          FROM registry.vehicles

        UNION ALL

        -- Temporarily assigned (US-13.9, AL-23). Three predicates, three requirements:
        --   revoked_at IS NULL      — US-13.8, the operator took it back;
        --   valid_from <= now()     — a relief driver booked for Thursday cannot drive today;
        --   expires_at > now()      — US-13.9's auto-expiry, with nobody revoking anything.
        -- The last is the whole of C059's "an assignment expiring removes the driver's ability
        -- to select that vehicle without manual action": the row stays exactly as it was and
        -- simply stops being returned.
        SELECT a.driver_id,
               a.vehicle_id,
               'assigned' AS source,
               a.fleet_id
          FROM registry.fleet_assignments a
         WHERE a.revoked_at IS NULL
           AND a.valid_from <= now()
           AND (a.expires_at IS NULL OR a.expires_at > now())
       ) AS candidate
  JOIN registry.vehicles v ON v.id = candidate.vehicle_id
 ORDER BY candidate.driver_id,
          candidate.vehicle_id,
          CASE candidate.source WHEN 'owned' THEN 0 ELSE 1 END;

COMMENT ON VIEW registry.driver_eligible_vehicles IS
  'Which vehicles a driver may go live on right now, and how they came by them (US-9.6, US-13.9). source = owned | assigned. An assigned row is returned only inside its validity window, so US-13.9''s auto-expiry needs no sweep and no revocation (C059). is_go_live_eligible is APPROVED + dispatch_state ACTIVE; the raw columns are kept so each consumer can map its own errors. registry-svc owns it; dispatch-svc, trip-state-svc and fleet-svc read it.';

-- -------------------------------------------------------------------------------------
-- (b) Fleet scheduling and the not-started alarm (US-13.11 / US-13.11b)
-- -------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS registry.fleet_schedules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  -- Mode A route, the same column trips.sessions.route_id names — and landing bare for the
  -- same reason it did in 0501: `spatial.routes` is created in the 14xx range and cannot be
  -- referenced from here. The FK is added by 1408, exactly as 1401 added trips.sessions'.
  route_id UUID,
  depart_at TIMESTAMPTZ NOT NULL,
  -- fleet.yaml bounds it 1..120 with a default of 10. SMALLINT because two hours in minutes
  -- is 120 and a column wide enough for a week of them invites one.
  not_started_alarm_minutes SMALLINT NOT NULL DEFAULT 10
    CONSTRAINT ck_fleet_schedules_alarm CHECK (not_started_alarm_minutes BETWEEN 1 AND 120),
  -- SCHEDULED -> STARTED when a trips.sessions row opens on the vehicle in the window;
  -- -> MISSED when the alarm offset passes with no session; -> CANCELLED by the operator.
  -- MISSED is terminal for the alarm and not for the journey: a bus that leaves forty minutes
  -- late still leaves, and the operator has already been told.
  status TEXT NOT NULL DEFAULT 'SCHEDULED'
    CONSTRAINT ck_fleet_schedules_status CHECK (status IN ('SCHEDULED','STARTED','MISSED','CANCELLED')),
  -- When the not-started alarm was raised. NOT a duplicate of status: it is what makes the
  -- sweep idempotent across replicas, and what a support ticket is answered from six weeks
  -- later ("we were never told") when the status has long since moved on.
  alarm_raised_at TIMESTAMPTZ,
  created_by UUID REFERENCES iam.users(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- One live schedule per vehicle per departure instant. Two managers entering the 06:10 from
-- the depot is the ordinary double-submit, and an idempotency key does not catch it — the two
-- requests are genuinely different HTTP calls minutes apart. CANCELLED is outside the
-- predicate so a cancelled slot can be re-entered.
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_schedules_slot
  ON registry.fleet_schedules(vehicle_id, depart_at) WHERE status <> 'CANCELLED';

-- The alarm sweep's claim: everything still SCHEDULED whose departure has passed.
CREATE INDEX IF NOT EXISTS ix_fleet_schedules_due
  ON registry.fleet_schedules(depart_at) WHERE status = 'SCHEDULED';
-- SCR-FP-008's list, newest first.
CREATE INDEX IF NOT EXISTS ix_fleet_schedules_fleet
  ON registry.fleet_schedules(fleet_id, depart_at DESC);

SELECT public.attach_set_updated_at('registry','fleet_schedules');

COMMENT ON TABLE registry.fleet_schedules IS
  'Per-vehicle scheduled departures and the US-13.11 not-started alarm (SCR-FP-008). NOT dispatch.scheduled_rides, which is a passenger''s Mode C advance booking — AL-03 forbids a fleet Mode C vehicle, and a bus leaving the depot has no passenger and no pickup point (C059 gap (b)).';
COMMENT ON COLUMN registry.fleet_schedules.not_started_alarm_minutes IS
  'Minutes after depart_at at which the assigned driver''s app rings and the Fleet Portal is notified if no session has opened (US-13.11/13.11b). 1..120, default 10, per fleet.yaml.';
COMMENT ON COLUMN registry.fleet_schedules.alarm_raised_at IS
  'When the not-started alarm actually fired. Separate from status so the sweep is idempotent across replicas and so "were we told?" is answerable after the status has moved on.';

-- -------------------------------------------------------------------------------------
-- (c) Bulk vehicle onboarding (US-13.1)
-- -------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS registry.fleet_bulk_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  requested_by UUID NOT NULL REFERENCES iam.users(id),
  status TEXT NOT NULL DEFAULT 'PROCESSING'
    CONSTRAINT ck_fleet_bulk_jobs_status CHECK (status IN ('PROCESSING','COMPLETED','FAILED')),
  total_rows INTEGER NOT NULL CHECK (total_rows >= 0 AND total_rows <= 5000),   -- the T-09 ceiling
  succeeded_rows INTEGER NOT NULL DEFAULT 0 CHECK (succeeded_rows >= 0),
  failed_rows INTEGER NOT NULL DEFAULT 0 CHECK (failed_rows >= 0),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  finished_at TIMESTAMPTZ);

-- `429 bulk-in-progress`, the same shape and the same reasoning as ux_bulk_jobs_in_flight
-- (0405): two Fleet Portal tabs submitting at once is exactly the race a SELECT-then-INSERT
-- loses, and the second upload would import a duplicate of every row in the first.
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_bulk_jobs_in_flight
  ON registry.fleet_bulk_jobs(fleet_id) WHERE status = 'PROCESSING';

CREATE INDEX IF NOT EXISTS ix_fleet_bulk_jobs_fleet
  ON registry.fleet_bulk_jobs(fleet_id, created_at DESC);

SELECT public.attach_set_updated_at('registry','fleet_bulk_jobs');

COMMENT ON TABLE registry.fleet_bulk_jobs IS
  'US-13.1 bulk vehicle onboarding from CSV. One PROCESSING job per fleet (ux_fleet_bulk_jobs_in_flight) is fleet.yaml''s 429 bulk-in-progress (C059 gap (c)).';

CREATE TABLE IF NOT EXISTS registry.fleet_bulk_job_rows (
  job_id UUID NOT NULL REFERENCES registry.fleet_bulk_jobs(id) ON DELETE CASCADE,
  row_number INTEGER NOT NULL,                                -- 1-based, as the CSV numbers it
  registration_number TEXT NOT NULL,
  vehicle_type TEXT,
  mode TEXT,
  mode_b_billing TEXT,
  -- Non-negative like every other `*_minor` on the platform, even though this one is a
  -- transcription of what the operator's CSV said rather than an amount anybody is charged:
  -- a negative fare here would be reported back as a valid row and then refused by
  -- `registry.vehicles.default_monthly_fare_minor`'s own CHECK, in a different vocabulary.
  default_monthly_fare_minor INTEGER CHECK (default_monthly_fare_minor >= 0),
  -- IMPORTED rows carry a vehicle; FAILED rows carry a code and no vehicle. There is no
  -- PENDING: fleet.yaml's bulk answer is 202 with a `status`, and the rows are validated and
  -- imported in one transaction — unlike prov's bulk, which has a per-row credential to mint
  -- afterwards. A vehicle row is an INSERT, so there is nothing to drain.
  status TEXT NOT NULL
    CONSTRAINT ck_fleet_bulk_job_rows_status CHECK (status IN ('IMPORTED','FAILED')),
  vehicle_id UUID REFERENCES registry.vehicles(id) ON DELETE SET NULL,
  error_code TEXT,                                            -- the kebab code, e.g. registration-exists
  error_detail TEXT,
  PRIMARY KEY (job_id, row_number),
  -- The report and the roster must agree: a row cannot claim to have imported nothing, nor to
  -- have failed for no reason.
  CONSTRAINT ck_fleet_bulk_job_rows_outcome CHECK (
    (status = 'IMPORTED' AND vehicle_id IS NOT NULL AND error_code IS NULL)
    OR (status = 'FAILED' AND vehicle_id IS NULL AND error_code IS NOT NULL)));

CREATE INDEX IF NOT EXISTS ix_fleet_bulk_job_rows_failed
  ON registry.fleet_bulk_job_rows(job_id, row_number) WHERE status = 'FAILED';

COMMENT ON TABLE registry.fleet_bulk_job_rows IS
  'One row per CSV line, and the source of the downloadable per-row error report (US-13.1). `error_code` is the same kebab registry the HTTP API uses, so the report and a single POST /vehicles fail with one vocabulary.';
COMMENT ON INDEX registry.ix_fleet_bulk_job_rows_failed IS
  'The error report is the failed rows only; a 5,000-row job with three bad lines must not scan 5,000.';
