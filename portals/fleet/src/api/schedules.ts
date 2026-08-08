import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

import type { Assignment } from './drivers';

/**
 * **SCR-FP-008** — per-vehicle scheduled departures and US-13.11's not-started
 * alarm, transcribed from `backend/contracts/fleet.yaml` and from the constants
 * `Fleet.Api` evaluates them under.
 *
 * Nothing here calls anything. The org-relative target is the string a screen
 * hands `read()`/`mutate()`, which is where the caller's own `fleetId` is written
 * into a URL and the only place it ever is (`./client`).
 *
 * ## The shapes live in `./insights` and are re-exported, not re-declared
 *
 * The dashboard's alerts card counts `MISSED` departures (C114), so
 * `FleetSchedule` was already transcribed there. One wire shape gets one
 * declaration — a second copy is how a contract change lands in one screen and
 * not the other. What is *this* screen's lives here: the write, the alarm's
 * bounds, the departure clock and the question of whose app rings.
 *
 * ## Three things the platform decides that this file only restates
 *
 * | fact | where it is decided | why the screen has to say it |
 * |---|---|---|
 * | the alarm bounds (1…120, default 10) | `ScheduleService` + 0314's CHECK | the form refuses out of range before the round trip |
 * | the list window (a day back) | `ListSchedulesAsync` | the table is not the whole history and must not read as one |
 * | early-start grace (30 min) | `Fleet:ScheduleEarlyStartGrace` | "On time" includes a bus that left half an hour early |
 *
 * `test/schedules.test.ts` pins all three against the C# and the contract, so a
 * retuned deployment is a failing test rather than a wrong caption.
 */

/* ---------------------------------------------------------------------------
 * Targets and shapes
 * ------------------------------------------------------------------------ */

export { FLEET_SCHEDULES } from './insights';
export type { FleetSchedule, FleetScheduleList, FleetScheduleStatus } from './insights';

import type { FleetSchedule, FleetScheduleStatus } from './insights';

/**
 * The body `POST /v1/fleets/{own id}/schedules` takes.
 *
 * `routeId` is offered by the contract and **not sent by this screen** — see
 * {@link ROUTE_PICKER_UNAVAILABLE}.
 */
export interface CreateScheduleBody {
  readonly vehicleId: string;
  readonly departAt: string;
  readonly notStartedAlarmMinutes: number;
}

/* ---------------------------------------------------------------------------
 * The alarm
 * ------------------------------------------------------------------------ */

/** `fleet.yaml`'s bounds on `notStartedAlarmMinutes`, and 0314's own CHECK. */
export const SCHEDULE_ALARM_MIN_MINUTES = 1;
export const SCHEDULE_ALARM_MAX_MINUTES = 120;

/** The contract's default: the alarm rings ten minutes after the booked departure. */
export const SCHEDULE_ALARM_DEFAULT_MINUTES = 10;

export function isAlarmMinutes(value: number): boolean {
  return (
    Number.isInteger(value) &&
    value >= SCHEDULE_ALARM_MIN_MINUTES &&
    value <= SCHEDULE_ALARM_MAX_MINUTES
  );
}

/**
 * **Every departure has an alarm, and the wireframe's toggle has nothing to
 * switch.**
 *
 * `registry.fleet_schedules.not_started_alarm_minutes` is `NOT NULL DEFAULT 10`
 * with `CHECK (… BETWEEN 1 AND 120)`, and `createFleetSchedule` has no field that
 * could mean "no alarm". So the sketch's off-state is not a state the platform
 * can hold: what an operator actually chooses is the *offset*, and choosing it is
 * the same act as booking the departure. The column therefore reports the offset
 * as a fact rather than drawing a switch that would post nowhere — the rule this
 * portal has followed since C111 for an affordance no route serves.
 */
export const ALARM_IS_ALWAYS_ON = true;

/* ---------------------------------------------------------------------------
 * The window the list covers
 * ------------------------------------------------------------------------ */

/**
 * `ListSchedulesAsync`'s default `from`: **a day back**, "so the departures whose
 * alarm just rang are on the screen an operator opens to find out why".
 *
 * Restated here because the table has to name the period it is showing. A screen
 * that silently listed one day of a year's schedules would read as a fleet that
 * had stopped scheduling.
 */
export const SCHEDULE_LOOKBACK_HOURS = 24;

/**
 * `Fleet:ScheduleEarlyStartGrace` — how early a session may open and still count
 * as making its departure.
 *
 * Part of what "On time" means, and not derivable from anything on the wire.
 */
export const SCHEDULE_EARLY_START_GRACE_MINUTES = 30;

/* ---------------------------------------------------------------------------
 * The departure clock
 * ------------------------------------------------------------------------ */

const COLOMBO = 'Asia/Colombo';

/** `<input type="datetime-local">`'s value — a wall clock with no zone on it. */
const LOCAL_DATE_TIME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::\d{2})?$/;

/**
 * Sri Lanka's UTC offset on a given day, as `+05:30`.
 *
 * Derived rather than written down. The country has observed one offset with no
 * daylight saving since 2006, so this is a single value today — but a zone rule
 * is data that governments change, and `Intl`'s copy of it is the one this
 * process already ships. The literal is the fallback for a runtime whose ICU
 * build cannot name an offset, never the answer.
 */
export function colomboOffset(at: Date): string {
  const name = new Intl.DateTimeFormat('en-GB', {
    timeZone: COLOMBO,
    timeZoneName: 'longOffset',
  })
    .formatToParts(at)
    .find((part) => part.type === 'timeZoneName')?.value;

  const match = /GMT([+-])(\d{2}):(\d{2})/.exec(name ?? '');
  return match ? `${match[1]}${match[2]}:${match[3]}` : '+05:30';
}

/**
 * A departure an operator typed, as the instant the platform will store.
 *
 * **This is the load-bearing conversion on the screen.** A `datetime-local` input
 * has no time zone: the browser hands back `2026-06-18T06:00`, meaning six in the
 * morning at the depot. The server action that reads it runs in a container set
 * to **UTC**, so `new Date('2026-06-18T06:00')` there is 11:30 in Colombo — a bus
 * booked out five and a half hours late, with an alarm to match. The wall clock is
 * therefore resolved against Asia/Colombo explicitly (D-13), never against the
 * process's own zone.
 *
 * `null` for anything that is not a wall clock, so the field can say so rather
 * than the service answering `400 validation-failed` about `departAt`.
 */
export function departAtFrom(local: string): string | null {
  const match = LOCAL_DATE_TIME.exec(local.trim());
  if (!match) return null;

  const [, year, month, day, hour, minute] = match;

  // The offset is read for the day in question rather than for today, so a rule
  // change between now and the departure is applied to the departure.
  const approximate = new Date(`${year}-${month}-${day}T${hour}:${minute}:00Z`);
  if (Number.isNaN(approximate.getTime())) return null;

  const instant = new Date(
    `${year}-${month}-${day}T${hour}:${minute}:00${colomboOffset(approximate)}`,
  );
  return Number.isNaN(instant.getTime()) ? null : instant.toISOString();
}

/** Now, as the wall clock the form's `min` attribute has to be written in. */
export function colomboLocalNow(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('sv-SE', {
    timeZone: COLOMBO,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  })
    .format(now)
    .replace(' ', 'T');
}

/**
 * Whether a departure is still bookable.
 *
 * `ScheduleService` refuses one in the past — "a departure in the past would be
 * swept into MISSED by the very next pass and would ring an alarm about a bus
 * that left this morning" — so the form checks it too, to answer on the field
 * instead of on the response. The service's clock is the authority; this only
 * decides whether it is worth asking.
 */
export function isFutureDeparture(departAt: string, now: Date = new Date()): boolean {
  const instant = Date.parse(departAt);
  return Number.isFinite(instant) && instant > now.getTime();
}

/* ---------------------------------------------------------------------------
 * Whose app rings
 * ------------------------------------------------------------------------ */

/**
 * The drivers whose app the not-started alarm would ring for a departure —
 * `FleetAssignmentRepository.DriversCoveringAsync`, predicate for predicate.
 *
 * ```sql
 * vehicle_id = @VehicleId AND revoked_at IS NULL
 *   AND valid_from <= @At AND (expires_at IS NULL OR expires_at > @At)
 * ```
 *
 * The vehicle is the first predicate and is not optional: `GET …/assignments`
 * answers the **organisation's** assignments, so a filter on the window alone
 * would ring every driver in the fleet for every departure.
 *
 * **`@At` is the booked departure, not now**, and that is the whole point:
 * "an alarm raised at 06:20 about the 06:10 belongs to the 06:10's driver, and a
 * shift that changed in between must not redirect it". So this is not the portal
 * computing `Assignment.active` — which is the database's to evaluate and which
 * this portal never re-derives — it is the same window arithmetic the alarm
 * worker does, over an instant that has nothing to do with the clock.
 *
 * A departure no assignment covers is the case `ScheduleAlarmWorker` logs as
 * "there is nobody to tell", and the screen says so before it happens.
 */
export function driversCovering(
  assignments: readonly Assignment[],
  vehicleId: string,
  departAt: string,
): readonly Assignment[] {
  const at = Date.parse(departAt);
  if (!Number.isFinite(at)) return [];

  return assignments.filter((assignment) => {
    if (assignment.vehicleId !== vehicleId) return false;
    if (assignment.revokedAt) return false;

    const from = Date.parse(assignment.from);
    if (!Number.isFinite(from) || from > at) return false;

    if (!assignment.to) return true;
    const to = Date.parse(assignment.to);
    return !Number.isFinite(to) || to > at;
  });
}

/* ---------------------------------------------------------------------------
 * What the platform has no route for
 * ------------------------------------------------------------------------ */

/**
 * **A booked departure cannot be changed or cancelled from anywhere.**
 *
 * `fleet.yaml` declares exactly two operations on `/v1/fleets/{fleetId}/schedules`
 * — `listFleetSchedules` and `createFleetSchedule`. There is no `PUT`, no `PATCH`,
 * no `DELETE` and no cancel verb, and no other contract carries one. `CANCELLED`
 * is a status 0314's CHECK admits and its comment attributes to "the operator",
 * but nothing on the platform can write it.
 *
 * The deliverable asks for "add/**change** scheduled rides", so this is a gap
 * rather than a decision, and the screen states it instead of drawing a control
 * that posts nowhere. Raised in the C115 handoff as a micro-change-set.
 * `test/schedules.test.ts` reads the contract and fails if a write appears that
 * this constant still claims does not exist.
 */
export const SCHEDULE_IS_WRITE_ONCE = true;

/**
 * **A route cannot be named or chosen here.**
 *
 * `FleetSchedule.routeId` is `registry.fleet_schedules.route_id`, whose foreign
 * key (migration 1408) is `spatial.routes` — the Mode A route table
 * `trips.sessions.route_id` also names. **No contract publishes those rows.**
 * `GET /v1/transit/routes/{routeId}` is transit-svc's and takes "GTFS `route_id`
 * from the active feed", which is a string in an entirely different id space; the
 * two cannot be joined by a client.
 *
 * So the wireframe's "138 Pettah ↔ Maharagama" has no source: the column shows the
 * reference a departure carries and the form sends none, because a picker with
 * nothing to list and a free-text UUID field are both worse than a sentence.
 */
export const ROUTE_PICKER_UNAVAILABLE = true;

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

export interface ScheduleStatusView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

/**
 * The status chip, in the wireframe's own four states.
 *
 * `MISSED` is **terminal for the alarm and not for the journey** — "a bus that
 * leaves forty minutes late still leaves, and the operator has already been told"
 * — so the label says the alarm rang rather than saying the bus never went.
 */
export function scheduleStatusView(status: FleetScheduleStatus): ScheduleStatusView {
  switch (status) {
    case 'STARTED':
      return { tone: 'success', labelKey: 'fleet.scheduling.status.started' };
    case 'MISSED':
      return { tone: 'error', labelKey: 'fleet.scheduling.status.missed' };
    case 'CANCELLED':
      return { tone: 'neutral', labelKey: 'fleet.scheduling.status.cancelled' };
    default:
      return { tone: 'info', labelKey: 'fleet.scheduling.status.scheduled' };
  }
}

/** Soonest first — the order `listFleetSchedules` answers in, restated for a merge. */
export function bySoonest(a: FleetSchedule, b: FleetSchedule): number {
  return Date.parse(a.departAt) - Date.parse(b.departAt);
}

/** How many departures on this page rang an alarm nobody has acted on. */
export function missedCount(schedules: readonly FleetSchedule[]): number {
  return schedules.filter((schedule) => schedule.status === 'MISSED').length;
}
