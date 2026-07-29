-- =====================================================================================
-- 0711 — dispatch: dispatch.timers gains a ride subject and a payload
-- Source: D5' §3.5 (global 2-minute timeout, US-6A.11) · ADD §11.12 (R-15 LWT grace) ·
--         server_db_schema.md §6 · C034 deliverables
--
-- ⚠ Spec gap — micro-change-set, raised in the C034 handoff.
--   `dispatch.timers` (0708) was printed for DT-04 alone, so its subject is a DRIVER and
--   `driver_id` is NOT NULL. C034 needs two more durable timers and only one of them has a
--   driver:
--     * `ride_timeout`         — US-6A.11's 120 s global cascade deadline. Its subject is a
--                                RIDE, and it has to fire even when no driver was ever found,
--                                which is exactly the case where no driver id exists.
--     * `offer_release_grace`  — R-15's "releases active offer / starts grace timer" when a
--                                driver's EMQX session drops. Subject is a driver.
--   server_db_schema.md §6 prints neither column. `rides.timers` (0605) cannot carry the first
--   one either: its `ck_timers_kind` CHECK is a closed list of eight ride-svc kinds, and
--   widening another bounded context's CHECK to hold a dispatch deadline is the worse trade —
--   dispatch already owns this table and 0708's own comment says "dispatch-svc adds kinds
--   without a migration".
--
--   **server_db_schema.md §6 / D4' §6 should print `ride_id`, `payload` and the nullable
--   `driver_id`** on `dispatch.timers`, or D5' §3.5 should say which table holds the global
--   deadline.
--
-- Adds no table: `migrate-verify.sh`'s "13 dispatch tables" check is unchanged.
-- =====================================================================================

-- A ride_timeout has no driver — that is the whole point of it (see the header). The CHECK
-- below is what keeps "nullable" from meaning "a timer with no subject at all".
ALTER TABLE dispatch.timers ALTER COLUMN driver_id DROP NOT NULL;

ALTER TABLE dispatch.timers
  ADD COLUMN IF NOT EXISTS ride_id UUID REFERENCES rides.rides(id) ON DELETE CASCADE;

-- Shaped like rides.timers.payload (0605): the row carries what it is expiring, so a sweep
-- expires *that* offer rather than whichever one the driver turns out to hold by the time the
-- timer runs.
ALTER TABLE dispatch.timers ADD COLUMN IF NOT EXISTS payload JSONB;

-- Idempotent: a re-run of this script must not fail on a constraint it already added, and
-- ADD CONSTRAINT has no IF NOT EXISTS before PG 17.
DO $$
BEGIN
  IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'ck_dispatch_timers_subject'
           AND conrelid = 'dispatch.timers'::regclass) THEN
    ALTER TABLE dispatch.timers
      ADD CONSTRAINT ck_dispatch_timers_subject
      CHECK (driver_id IS NOT NULL OR ride_id IS NOT NULL);
  END IF;
END $$;

-- One live timer per (subject, kind). This is the idempotency mechanism for arming, not an
-- optimisation: `ride.requested` is delivered at least once (D6' §2.3) and every redelivery
-- would otherwise arm a second 120-second deadline for the same ride, each of which would then
-- try to cancel it.
CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_timers_ride_live
  ON dispatch.timers(ride_id, kind) WHERE fired_at IS NULL AND ride_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_timers_driver_live
  ON dispatch.timers(driver_id, kind) WHERE fired_at IS NULL AND driver_id IS NOT NULL;

-- The sweep claims by (kind, fire_at); ix_dispatch_timers_due (0708) orders by fire_at alone,
-- which makes every kind's sweep read every other kind's due rows.
CREATE INDEX IF NOT EXISTS ix_dispatch_timers_kind_due
  ON dispatch.timers(kind, fire_at) WHERE fired_at IS NULL;

COMMENT ON COLUMN dispatch.timers.ride_id IS
  'The ride a timer is about, for kinds whose subject is a ride (ride_timeout, US-6A.11). NULL for the driver-subject kinds 0708 was printed for (directional_expiry, offer_release_grace).';
COMMENT ON COLUMN dispatch.timers.driver_id IS
  'The driver a timer is about. Nullable since 0711: a ride_timeout fires precisely when no driver was found.';
COMMENT ON COLUMN dispatch.timers.payload IS
  'What the timer is expiring (offerId, reason). Same shape and same reason as rides.timers.payload (0605).';
COMMENT ON INDEX dispatch.ux_dispatch_timers_ride_live IS
  'One live timer per (ride, kind) — the arming idempotency for at-least-once ride.events delivery (D6'' §2.3).';
