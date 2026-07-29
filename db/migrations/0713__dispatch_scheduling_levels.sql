-- =====================================================================================
-- 0713 — dispatch: what the Job Board, the Driver Level engine and the Rs 50 ledger need
-- Source: D5' §3.7 (Job Board) · D5' §4 (Driver Level System) · D5' §7.1 (cross-trip Rs 50)
--         · D3' dispatch-svc route table · server_db_schema.md §6 · D4' §6
--         · US-6A.4/6A.5/6A.6/6A.7/6A.8/6A.14/6A.15, US-14.12, D-05, D-06, AL-16, AL-36
--
-- ⚠ Spec gaps — micro-change-sets, raised in the C035 handoff. Every change below is a column,
--   an index or one singleton config table that the endpoints D3' *already* prints cannot be
--   implemented without. Grouped rather than split so one file carries one component's argument.
--
--   (a) **A scheduled ride has no payment method.** `POST /v1/rides/schedule` (D3' Δ 2026-06-28,
--       AL-36) takes destination, pickup, time and tier and nothing else — but at T-30 min the
--       row becomes a `rides.rides`, whose `payment_method` is NOT NULL and whose CHECK is a
--       closed set. Booking a ride is choosing how to pay for it; hard-coding `cash` in the
--       service would take that choice away silently. The column defaults to `cash` (D5' §8's
--       default and the only method needing no pre-authorisation), so a client written against
--       the printed contract still works. **D3' should print `paymentMethod` on the schedule
--       body**; `dispatch.yaml` carries it as an optional property (Δ C035).
--
--   (b) **`points_awarded_total` is what makes the level-up engine idempotent.** D5' §4.2 says
--       "500 points = +1 level" and `dispatch.driver_levels.rating_points` is the *remainder*
--       after a level-up, not a running total — so nothing in the printed DDL records which
--       ratings have already been counted. Without a watermark the engine can only be a
--       consume-once queue (a second table) or a double-counter. One integer is the smaller
--       change: points are recomputed from `trips.ratings` and only the delta is applied, which
--       is idempotent under replay, under a crash mid-update and under two replicas racing.
--
--   (c) **A level decrement must be once per no-show, not once per delivery.** `dispatch.
--       no_show_events` (0705) is append-only with no uniqueness, and D6' §2.3 delivery is
--       at-least-once — so a redelivered no-show would take a second level off a driver who
--       missed one ride. The partial unique index is the idempotency, in the same place and for
--       the same reason as `ux_dispatch_timers_ride_live` (0711).
--
--   (d) **The penalty ledger cannot say which rule it came from, and cannot refuse a
--       redelivery.** D5' §7.1's row is written from `cancellation.penalty.accrued`, which
--       carries three bases (§11.12: the Rs 50 `cancellation_fee`, the Rs 100 `no_show_fee`,
--       the mid-trip `full_fare`), all three marked `settledOn: next_trip`. `basis` is what
--       tells fare-svc whether the stored amount is the amount or a rule to re-evaluate —
--       `full_fare` is the *quoted* fare and fare-svc replaces it with the metered one. The
--       unique index is the at-least-once guard: one penalty per (ride, basis).
--       `ux_penalty_apply` (0706) does not do this job and its own header says so.
--
--   (e) **`PUT /v1/admin/drivers/level-config` has nowhere to write.** D3' puts it on
--       dispatch-svc and US-14.12 makes the level parameters admin-tunable at runtime;
--       `dispatch.driver_levels.level_up_threshold` is per-driver and cannot hold a
--       platform-wide setting. Same shape and same argument as `dispatch.directional_config`
--       (0707): one row, changed by an admin route, and every replica must agree instantly.
--
-- Adds ONE table: `migrate-verify.sh`'s dispatch table count moves 13 → 14 in the same change.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (a) Scheduled rides: the payment method the materialised ride will carry, and the two
--     indexes the T-30 scheduler and the re-offer cascade read.
-- -------------------------------------------------------------------------------------

ALTER TABLE dispatch.scheduled_rides
  ADD COLUMN IF NOT EXISTS payment_method TEXT NOT NULL DEFAULT 'cash';

-- The same closed set as rides.rides.payment_method (0601). Spelled again rather than
-- referenced because a scheduled ride that cannot be materialised is worse than one that is
-- refused at booking time: the passenger finds out 30 minutes before the pickup.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'dispatch.scheduled_rides'::regclass
                    AND conname = 'ck_scheduled_rides_payment_method') THEN
    ALTER TABLE dispatch.scheduled_rides
      ADD CONSTRAINT ck_scheduled_rides_payment_method
      CHECK (payment_method IN ('cash','lankaqr','onepay','cod'));
  END IF;
END $$;

-- One scheduled ride materialises exactly one `rides.rides` row, and a ride belongs to at most
-- one scheduled ride. This is also the idempotency of the T-30 materialisation: a worker that
-- died between the ride-svc call and the status flip retries against the same ride.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sched_ride
  ON dispatch.scheduled_rides(ride_id) WHERE ride_id IS NOT NULL;

-- The passenger's own list, and the AL-36 "my scheduled rides" view.
CREATE INDEX IF NOT EXISTS ix_sched_passenger
  ON dispatch.scheduled_rides(passenger_id, pickup_time DESC);

-- -------------------------------------------------------------------------------------
-- (b) The Driver Level engine's watermark.
-- -------------------------------------------------------------------------------------

ALTER TABLE dispatch.driver_levels
  ADD COLUMN IF NOT EXISTS points_awarded_total INTEGER NOT NULL DEFAULT 0;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'dispatch.driver_levels'::regclass
                    AND conname = 'ck_driver_levels_points_awarded') THEN
    ALTER TABLE dispatch.driver_levels
      ADD CONSTRAINT ck_driver_levels_points_awarded CHECK (points_awarded_total >= 0);
  END IF;
END $$;

COMMENT ON COLUMN dispatch.driver_levels.points_awarded_total IS
  'Every rating point ever counted for this driver (D5'' §4.2). rating_points is the remainder after level-ups; this is the watermark that makes recomputing from trips.ratings idempotent.';

-- -------------------------------------------------------------------------------------
-- (c) One level decrement per (driver, no-show), whatever the delivery count.
-- -------------------------------------------------------------------------------------

CREATE UNIQUE INDEX IF NOT EXISTS ux_no_show_driver_ride
  ON dispatch.no_show_events(driver_id, ride_id) WHERE ride_id IS NOT NULL;

COMMENT ON INDEX dispatch.ux_no_show_driver_ride IS
  'US-6A.7 is one decrement per missed ride. D6'' §2.3 delivery is at-least-once and POST /v1/internal/drivers/{id}/no-show may be retried, so the insert is the claim: no row written, no level taken.';

-- -------------------------------------------------------------------------------------
-- (d) The penalty ledger: which rule accrued it, and one row per accrual.
-- -------------------------------------------------------------------------------------

ALTER TABLE dispatch.cancellation_penalties
  ADD COLUMN IF NOT EXISTS basis TEXT NOT NULL DEFAULT 'cancellation_fee';

-- The three §11.12 rows whose penalty is settled on the passenger's next completed trip. The
-- names are ride-svc's own (`RideCancellationService.PenaltyBasisName`), because this row is
-- built from its event and fare-svc reads both.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'dispatch.cancellation_penalties'::regclass
                    AND conname = 'ck_cancellation_penalties_basis') THEN
    ALTER TABLE dispatch.cancellation_penalties
      ADD CONSTRAINT ck_cancellation_penalties_basis
      CHECK (basis IN ('cancellation_fee','no_show_fee','full_fare'));
  END IF;
END $$;

-- A ride accrues at most one penalty per basis. This is the at-least-once guard on the
-- `cancellation.penalty.accrued` consumer; `ux_penalty_apply` (0706) guards nothing on its own
-- and its own header says why.
CREATE UNIQUE INDEX IF NOT EXISTS ux_penalty_accrual
  ON dispatch.cancellation_penalties(original_ride_id, basis);

COMMENT ON COLUMN dispatch.cancellation_penalties.basis IS
  'Which §11.12 rule accrued this debt. `full_fare` means amount_minor is the QUOTED fare and fare-svc settles the metered one instead (D5'' §7.1); the other two are the amount.';
COMMENT ON INDEX dispatch.ux_penalty_accrual IS
  'One accrual per (ride, basis) — the idempotency of the at-least-once cancellation.penalty.accrued consumer (D6'' §2.3).';

-- -------------------------------------------------------------------------------------
-- (e) Platform-wide Driver Level parameters (US-14.12), single row id=1.
-- -------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS dispatch.level_config (
  id SMALLINT PRIMARY KEY DEFAULT 1
    CONSTRAINT ck_level_config_singleton CHECK (id = 1),
  -- D5' §4.2's 500. Per-driver `driver_levels.level_up_threshold` is a mirror of this value,
  -- kept in step by the engine so GET /v1/drivers/{id}/level reports the threshold that is
  -- actually in force.
  level_up_threshold INTEGER NOT NULL DEFAULT 500
    CONSTRAINT ck_level_config_threshold CHECK (level_up_threshold >= 1),
  -- Rating points taken by a no-show and by a driver cancellation. **No spec gives either
  -- number**; both default to 0, so out of the box a no-show costs the level D5' §4.2 names
  -- and nothing else, and an admin who wants a points penalty as well can set one.
  no_show_penalty_points INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_level_config_no_show_points CHECK (no_show_penalty_points >= 0),
  cancellation_penalty_points INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_level_config_cancel_points CHECK (cancellation_penalty_points >= 0),
  -- US-6A.8: Level 1 loses the Job Board and scheduled rides. Expressed as the minimum level
  -- that keeps them rather than as "not 1", because it is the number the admin surface sets.
  job_board_min_level SMALLINT NOT NULL DEFAULT 2
    CONSTRAINT ck_level_config_min_level CHECK (job_board_min_level BETWEEN 1 AND 3),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('dispatch','level_config');

INSERT INTO dispatch.level_config(id) VALUES (1) ON CONFLICT (id) DO NOTHING;

COMMENT ON TABLE dispatch.level_config IS
  'Admin-configurable Driver Level parameters (US-14.12, PUT /v1/admin/drivers/level-config), exactly one row. Defaults are D5'' §4.2''s: 500 points per level, Level 1 excluded from the Job Board.';
