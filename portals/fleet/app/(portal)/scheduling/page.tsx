import { redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ASSIGNMENTS, type Assignment, type AssignmentList } from '@/api/drivers';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  bySoonest,
  colomboLocalNow,
  missedCount,
  FLEET_SCHEDULES,
  SCHEDULE_ALARM_DEFAULT_MINUTES,
  SCHEDULE_ALARM_MAX_MINUTES,
  SCHEDULE_ALARM_MIN_MINUTES,
  SCHEDULE_EARLY_START_GRACE_MINUTES,
  SCHEDULE_LOOKBACK_HOURS,
  type FleetSchedule,
  type FleetScheduleList,
} from '@/api/schedules';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScheduleForm } from '@/components/scheduling/ScheduleForm';
import { ScheduleTable } from '@/components/scheduling/ScheduleTable';
import { schedulableVehicles, scheduleRows } from '@/components/scheduling/schedule-model';
import { getLocale, getTranslator } from '@/i18n/server';
import { canMutate, isApproved } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-008 · `fleet_scheduling`** — per-vehicle scheduled departures and
 * US-13.11's not-started alarm.
 *
 * ## Three reads, and only one of them can take the screen down
 *
 * | part | route | if it fails |
 * |---|---|---|
 * | the departures | `GET …/schedules` | the screen shows a problem panel |
 * | plates and the picker | `GET …/vehicles` | rows lose their plate, the form has nothing to book |
 * | whose app rings | `GET …/assignments` | the driver line reads "nobody assigned" |
 *
 * The last two are **approval-gated** (`FleetVehiclesGroup` and
 * `FleetAssignmentsGroup` both carry `RequireApprovedFleet()`) and the schedule
 * list is not, which is why this screen opens for a pending organisation with an
 * empty table rather than being hidden — refusing a read the platform allows is
 * this portal inventing a refusal (C111 decision 3).
 *
 * ## The write is gated where the endpoint is, and the read is not
 *
 * `POST …/schedules` carries `RequireFleetSubRole(Manager)` **and**
 * `RequireApprovedFleet()`; `GET …/schedules` carries neither beyond Viewer. So a
 * Viewer and a pending organisation both get the table and a sentence in place of
 * the form — the same shape SCR-FP-006 has, and for the same reason.
 *
 * ## Two things the wireframe draws that the platform does not serve
 *
 * 1. **A route name.** `FleetSchedule.routeId` is a `spatial.routes` id and
 *    nothing on any contract lists those rows — `GET /v1/transit/routes/{routeId}`
 *    is transit-svc's and takes a GTFS string, which is a different id space. The
 *    column shows the reference a departure carries; the form sends none.
 * 2. **An alarm toggle, and any later edit.** The alarm column is `NOT NULL` with
 *    a 1…120 CHECK, so there is no "off"; and `/schedules` has exactly two
 *    operations, so a booked departure cannot be changed or cancelled by anybody.
 *    Both are stated on the screen and raised in the C115 handoff.
 */

export const dynamic = 'force-dynamic';

export default async function SchedulingPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const approved = isApproved(session);
  const mayBook = canMutate(session, 'fleet-operations', { requiresApprovedOrg: true });

  let schedules: readonly FleetSchedule[] = [];
  let problem: ProblemDetails | null = null;
  try {
    const answer = await read<FleetScheduleList>({ org: FLEET_SCHEDULES });
    schedules = [...(answer.items ?? [])].sort(bySoonest);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const [roster, assignments] = await Promise.all([optionalRoster(), optionalAssignments()]);

  const rows = scheduleRows({
    schedules,
    vehicles: new Map(roster.map((vehicle) => [vehicle.vehicleId, vehicle])),
    assignments,
    locale,
    t,
  });

  const missed = missedCount(schedules);

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.scheduling.title')}</h2>

        {missed > 0 ? (
          <StatusPill tone="error">{t('fleet.scheduling.missedCount', { count: missed })}</StatusPill>
        ) : null}

        {mayBook ? (
          // The sketch's topbar control. An anchor rather than a button, because
          // the form it opens is on the same page and a Viewer's session must not
          // render a control at all — not a control that scrolls to nothing.
          <a
            href="#schedule-ride"
            className="inline-flex h-10 items-center justify-center rounded-sm bg-primary px-md text-body-sm font-body text-on-primary hover:bg-primary/90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
          >
            {t('fleet.scheduling.book.open')}
          </a>
        ) : null}
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {mayBook ? (
        <ScheduleForm
          vehicles={schedulableVehicles(roster, t)}
          minDepartAt={colomboLocalNow()}
          defaultAlarmMinutes={SCHEDULE_ALARM_DEFAULT_MINUTES}
          alarmRange={{ min: SCHEDULE_ALARM_MIN_MINUTES, max: SCHEDULE_ALARM_MAX_MINUTES }}
          labels={{
            heading: t('fleet.scheduling.book.heading'),
            vehicle: t('fleet.scheduling.field.vehicle'),
            departAt: t('fleet.scheduling.field.departAt'),
            departAtHint: t('fleet.scheduling.field.departAtHint'),
            alarm: t('fleet.scheduling.field.alarm'),
            alarmHint: t('fleet.scheduling.field.alarmHint', {
              min: SCHEDULE_ALARM_MIN_MINUTES,
              max: SCHEDULE_ALARM_MAX_MINUTES,
              grace: SCHEDULE_EARLY_START_GRACE_MINUTES,
            }),
            required: t('fleet.org.required'),
            submit: t('fleet.scheduling.book.submit'),
            submitting: t('fleet.scheduling.book.submitting'),
            noVehicles: t('fleet.scheduling.book.noVehicles'),
            noRoute: t('fleet.scheduling.routeNote'),
          }}
        />
      ) : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {/*
            Two different refusals: an unapproved organisation is waiting on a
            Verification Officer, and a Viewer's seat is never going to carry this.
          */}
          {approved ? t('fleet.scheduling.viewerNotice') : t('fleet.scheduling.pendingOrg')}
        </p>
      )}

      <ScheduleTable
        rows={rows}
        labels={{
          heading: t('fleet.scheduling.table.heading'),
          caption: t('fleet.scheduling.table.caption'),
          vehicle: t('fleet.scheduling.column.vehicle'),
          route: t('fleet.scheduling.column.route'),
          start: t('fleet.scheduling.column.start'),
          alarm: t('fleet.scheduling.column.alarm'),
          status: t('fleet.scheduling.column.status'),
          empty: approved
            ? t('fleet.scheduling.table.empty')
            : t('fleet.scheduling.table.emptyPending'),
          alarmNote: t('fleet.scheduling.alarmNote', {
            grace: SCHEDULE_EARLY_START_GRACE_MINUTES,
          }),
          windowNote: t('fleet.scheduling.windowNote', { hours: SCHEDULE_LOOKBACK_HOURS }),
          routeNote: t('fleet.scheduling.routeNote'),
          writeOnceNote: t('fleet.scheduling.writeOnceNote'),
          noRoute: t('fleet.scheduling.route.none'),
        }}
      />
    </div>
  );
}

/**
 * The roster, for the plates and the picker.
 *
 * Best effort: `FleetVehiclesGroup` carries `RequireApprovedFleet()`, so a pending
 * organisation reads its departures — of which it has none — with no plates rather
 * than not at all.
 */
async function optionalRoster(): Promise<readonly FleetVehicle[]> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return [];
  }
}

/** The assignments, for "whose app rings". Same gate, same fallback. */
async function optionalAssignments(): Promise<readonly Assignment[]> {
  try {
    const answer = await read<AssignmentList>({ org: ASSIGNMENTS });
    return answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return [];
  }
}
