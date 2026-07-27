-- =====================================================================================
-- 0704 — dispatch: advance bookings and the Job Board
-- Source: server_db_schema.md §6, §23 · D4' §6 · ADD §9.1 · D5' §3.7
--         · US-6A.4, US-6A.5, D-06, AL-36
--
-- Fence: scheduled rides live HERE, owned by dispatch-svc. ADD §1.11 AL-36 and one D3' Δ
-- heading name a `scheduling-svc` / `scheduling.scheduled_rides` that exist nowhere else in
-- the specs; ADD §9.1, D4' §6, server_db_schema §6 and D3' (line "Scheduled rides are owned by
-- dispatch-svc over dispatch.scheduled_rides") all agree on this placement. See planner
-- finding 2 in build/progress.md. There is no `scheduling` schema.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.scheduled_rides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id),                    -- NULL until dispatch materialises it
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  pickup_geo GEOGRAPHY(POINT,4326) NOT NULL,
  -- NOT NULL is the whole of AL-36 item 2: "select the location to go" needed no DDL change
  -- because the column was already mandatory; POST /v1/rides/schedule rejects a missing one.
  dropoff_geo GEOGRAPHY(POINT,4326) NOT NULL,
  vehicle_type TEXT NOT NULL,
  pickup_time TIMESTAMPTZ NOT NULL,
  status TEXT NOT NULL DEFAULT 'SCHEDULED' CONSTRAINT ck_scheduled_rides_status
    CHECK (status IN ('SCHEDULED','DISPATCHED','CANCELLED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- D-06: GET /v1/rides/job-board?radius=30km is an ST_DWithin over pickup_geo.
CREATE INDEX IF NOT EXISTS ix_sched_pickup
  ON dispatch.scheduled_rides USING gist(pickup_geo);
CREATE INDEX IF NOT EXISTS ix_sched_due
  ON dispatch.scheduled_rides(pickup_time) WHERE status = 'SCHEDULED';

-- Driver intent on a Job Board item (US-6A.5). One intent per driver per scheduled ride;
-- re-posting is an upsert, not a second row.
CREATE TABLE IF NOT EXISTS dispatch.job_board_intents (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scheduled_ride_id UUID NOT NULL
    REFERENCES dispatch.scheduled_rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_job_board_intent UNIQUE (scheduled_ride_id, driver_id));

CREATE INDEX IF NOT EXISTS ix_job_board_intents_driver
  ON dispatch.job_board_intents(driver_id, ts DESC);

COMMENT ON TABLE dispatch.scheduled_rides IS
  'Advance bookings (US-6A.4). Owned by dispatch-svc — there is no scheduling-svc and no scheduling schema (planner finding 2).';
COMMENT ON TABLE dispatch.job_board_intents IS
  'Driver intent submissions for a scheduled ride (US-6A.5). Level-1 drivers lose Job Board access (D5'' §4.3).';
