import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import {
  ASSIGNMENTS,
  assignmentStatusView,
  byActiveThenRecent,
  type Assignment,
  type AssignmentList,
} from '@/api/drivers';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { AssignDriverForm } from '@/components/drivers/AssignDriverForm';
import { AssignmentTable, type AssignmentRow } from '@/components/drivers/AssignmentTable';
import { ProblemPanel } from '@/components/ProblemPanel';
import { vehicleTypeLabel } from '@/components/vehicles/vehicle-model';
import { formatDay } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { canMutate } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-005 · `fleet_drivers`** — assign a driver to vehicles, revoke an
 * assignment, and the history of both (US-13.2, US-13.8, AL-23).
 *
 * ## Two reads, because a vehicle picker needs vehicles
 *
 * `GET …/assignments` answers with `registrationNumber` on each row, so the
 * *table* needs nothing else. The **assign form** does: it offers the vehicles a
 * driver can be put on, and only the roster knows what those are. Both routes sit
 * inside approval-gated groups (`FleetAssignmentsGroup` and
 * `FleetVehiclesGroup`), so a caller who reaches this screen can make both — and
 * `proxy.ts` has already sent a pending organisation to `/pending`.
 *
 * The roster read failing is not fatal to the screen: the table still renders and
 * the form says there is nothing to assign to. The assignments read failing is,
 * and shows a problem panel — that is the screen.
 *
 * ## A Viewer sees the history and no buttons
 *
 * `canMutate(session, 'fleet-operations', { requiresApprovedOrg: true })` decides
 * the form and the revoke buttons together, because both are the same URD §2.3
 * row behind the same group gate.
 */

export const dynamic = 'force-dynamic';

export default async function DriversPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const mayAssign = canMutate(session, 'fleet-operations', { requiresApprovedOrg: true });

  let assignments: readonly Assignment[] = [];
  let problem: ProblemDetails | null = null;
  try {
    const answer = await read<AssignmentList>({ org: ASSIGNMENTS });
    assignments = answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const vehicles = await roster();

  return (
    <div className="flex flex-col gap-md">
      <h2 className="text-title font-display">{t('fleet.drivers.title')}</h2>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {mayAssign ? (
        <AssignDriverForm
          vehicles={vehicles.map((vehicle) => ({
            vehicleId: vehicle.vehicleId,
            label: `${vehicle.registrationNumber} · ${vehicleTypeLabel(vehicle.vehicleType, t)}`,
          }))}
          labels={{
            heading: t('fleet.drivers.assign.heading'),
            driver: t('fleet.drivers.field.driver'),
            driverHint: t('fleet.drivers.field.driverHint'),
            vehicles: t('fleet.drivers.field.vehicles'),
            vehiclesHint: t('fleet.drivers.field.vehiclesHint'),
            from: t('fleet.drivers.field.from'),
            fromHint: t('fleet.drivers.field.fromHint'),
            to: t('fleet.drivers.field.to'),
            toHint: t('fleet.drivers.field.toHint'),
            required: t('fleet.org.required'),
            submit: t('fleet.drivers.assign.submit'),
            submitting: t('fleet.drivers.assign.submitting'),
            temporary: t('fleet.drivers.temporary'),
            noInvite: t('fleet.drivers.noInvite'),
            noVehicles: t('fleet.drivers.noVehicles'),
            done: (count) =>
              count === 1
                ? t('fleet.drivers.assign.doneOne')
                : t('fleet.drivers.assign.done', { count }),
          }}
        />
      ) : (
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {t('fleet.drivers.viewerNotice')}
        </p>
      )}

      <AssignmentTable
        rows={[...assignments].sort(byActiveThenRecent).map((assignment) => row(assignment, locale, t))}
        canRevoke={mayAssign}
        labels={{
          heading: t('fleet.drivers.table.heading'),
          caption: t('fleet.drivers.table.caption'),
          driver: t('fleet.drivers.column.driver'),
          vehicle: t('fleet.drivers.column.vehicle'),
          since: t('fleet.drivers.column.since'),
          until: t('fleet.drivers.column.until'),
          status: t('fleet.drivers.column.status'),
          actions: t('fleet.drivers.column.actions'),
          empty: t('fleet.drivers.table.empty'),
          revoke: t('fleet.drivers.revoke'),
          revoking: t('fleet.drivers.revoking'),
          revokeNote: t('fleet.drivers.revokeNote'),
          history: t('fleet.drivers.history'),
        }}
      />
    </div>
  );
}

/**
 * One assignment as the table renders it.
 *
 * The driver cell prefers the name and falls back to the id — `driverName` is
 * optional on the wire and a fleet that assigned by phone before the driver
 * filled in their profile has rows with none. **The id is never dropped**: it is
 * what an operator quotes to MageRide support, and `DRV-…` in the sketch is the
 * same value.
 */
function row(assignment: Assignment, locale: Locale, t: FleetTranslator): AssignmentRow {
  const status = assignmentStatusView(assignment);
  const until = formatDay(locale, assignment.to);

  return {
    assignmentId: assignment.assignmentId,
    driver: assignment.driverName ?? assignment.driverId,
    driverSecondary: assignment.driverName
      ? (assignment.driverPhone ?? assignment.driverId)
      : (assignment.driverPhone ?? null),
    vehicle: assignment.registrationNumber ?? assignment.vehicleId,
    since: formatDay(locale, assignment.from) ?? assignment.from,
    until: until ?? t('fleet.drivers.openEnded'),
    status: t(status.labelKey),
    statusTone: status.tone,
    // Only an assignment that is still standing can be ended. `DELETE` on one
    // already revoked or expired is a button whose only outcome is a 404.
    revocable: assignment.active && !assignment.revokedAt,
  };
}

/**
 * The vehicles the picker offers.
 *
 * An empty list rather than a failure: the assignment history is this screen, and
 * a roster read that 403s or 404s should cost the operator the *form*, not the
 * page. The form says there is nothing to assign to, which is also what an
 * organisation with no vehicles yet sees.
 */
async function roster(): Promise<readonly FleetVehicle[]> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return answer.items ?? [];
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return [];
  }
}
