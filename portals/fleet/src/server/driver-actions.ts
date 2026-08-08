'use server';

import { revalidatePath } from 'next/cache';

import {
  ASSIGNMENTS,
  assignmentTarget,
  driverReferenceFor,
  type AssignDriverBody,
  type Assignment,
} from '@/api/drivers';
import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-005's two writes** — assign a driver to vehicles, and revoke one
 * (US-13.2, US-13.8, AL-23).
 *
 * ## One driver, one or more vehicles, one press
 *
 * US-13.2: an assignment "links an existing MageRide Driver App user … to **one
 * or more** fleet vehicles". The contract's `POST …/assignments` takes a single
 * `vehicleId`, so the screen's multi-select becomes one request per vehicle —
 * fanned out here rather than in the browser, because a client loop would leave
 * a half-finished assignment behind an interrupted page.
 *
 * They are **not** a transaction and must not be reported as one. Each request
 * has its own `Idempotency-Key` and its own outcome; a driver already holding an
 * open assignment on one of the chosen vehicles is a `409` on that vehicle alone
 * ("a driver may hold only one open assignment per vehicle at a time"), and the
 * other vehicles are assigned. So the action returns what succeeded and what did
 * not, and the screen names both — an all-or-nothing message would either hide
 * three good assignments or claim a fourth that was refused.
 *
 * ## The window is what makes an assignment temporary (AL-23)
 *
 * `from` is required and `to` is optional, and that pair is the whole of
 * "temporarily hired drivers": an assignment with a `to` **auto-expires** with
 * nothing written and nobody pressing anything (US-13.9), which is why the screen
 * offers the end date rather than expecting somebody to remember to revoke.
 */

export interface DriverActionState {
  /** The failure, already translated. */
  readonly message?: string;
  readonly field?: 'driver' | 'vehicleIds' | 'from' | 'to';
  /** How many vehicles the driver was assigned to. */
  readonly assigned?: number;
  /** The plates that were refused, with the sentence for each. */
  readonly refused?: readonly { readonly vehicleId: string; readonly message: string }[];
  readonly revoked?: true;
}

/**
 * `fleet-operations` write, inside `FleetAssignmentsGroup`'s
 * `RequireApprovedFleet()`. Both transcribed, neither chosen.
 */
const REQUIRES = { area: 'fleet-operations', requiresApprovedOrg: true } as const;

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/**
 * `POST /v1/fleets/{id}/assignments`, once per selected vehicle.
 *
 * `from` defaults to **now** rather than to a date the operator has to pick: the
 * ordinary case is a driver starting on the vehicle they are standing next to,
 * and a required start date turns that into a form. A date typed into the
 * optional fields is read as Colombo local midnight — see {@link instantFrom}.
 */
export async function assignDriver(
  _state: DriverActionState,
  formData: FormData,
): Promise<DriverActionState> {
  const t = await getTranslator();

  const driver = driverReferenceFor(text(formData, 'driver'));
  if (driver.kind === 'unrecognised') {
    return { message: t('fleet.drivers.error.driverRequired'), field: 'driver' };
  }

  const vehicleIds = formData
    .getAll('vehicleIds')
    .map((value) => String(value).trim())
    .filter(Boolean);

  if (vehicleIds.length === 0) {
    return { message: t('fleet.drivers.error.vehicleRequired'), field: 'vehicleIds' };
  }

  const from = instantFrom(text(formData, 'from')) ?? new Date().toISOString();
  const to = instantFrom(text(formData, 'to'), { endOfDay: true });

  if (to && Date.parse(to) <= Date.parse(from)) {
    return { message: t('fleet.drivers.error.windowInverted'), field: 'to' };
  }

  const refused: { vehicleId: string; message: string }[] = [];
  let assigned = 0;

  for (const vehicleId of vehicleIds) {
    const body: AssignDriverBody = {
      vehicleId,
      from,
      ...(to ? { to } : {}),
      ...(driver.kind === 'id' ? { driverId: driver.driverId } : { driverPhone: driver.driverPhone }),
    };

    try {
      await mutate<Assignment, AssignDriverBody>({
        method: 'POST',
        org: ASSIGNMENTS,
        body,
        requires: REQUIRES,
      });
      assigned += 1;
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      refused.push({ vehicleId, message: t(error.messageKey) });
    }
  }

  revalidatePath('/drivers');

  return { assigned, ...(refused.length > 0 ? { refused } : {}) };
}

/**
 * `DELETE /v1/fleets/{id}/assignments/{assignmentId}` — US-13.8.
 *
 * "Ends the assignment now; an in-flight session is left to end normally." The
 * driver loses the ability to start a **new** session immediately, which is the
 * story's own wording and worth the screen saying, because an operator revoking a
 * driver mid-route would otherwise expect the bus to stop.
 */
export async function revokeAssignment(
  _state: DriverActionState,
  formData: FormData,
): Promise<DriverActionState> {
  const t = await getTranslator();

  const assignmentId = text(formData, 'assignmentId');
  if (!assignmentId) return { message: t('fleet.drivers.error.assignmentRequired') };

  try {
    await mutate({
      method: 'DELETE',
      org: assignmentTarget(assignmentId),
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/drivers');

  return { revoked: true };
}

/**
 * A `<input type="date">` value → the instant that date begins, or ends, **in
 * Colombo**.
 *
 * `+05:30` is written out rather than resolved through a time zone database
 * because Sri Lanka has one offset and has had since 2006, and because the
 * alternative — building a `Date` from the bare `YYYY-MM-DD`, which JavaScript
 * reads as UTC midnight — would start an assignment at 05:30 local and end one at
 * 05:29 the following morning. An end date is inclusive: an operator writing
 * "to 30 June" means the driver has that day.
 */
function instantFrom(value: string, options: { endOfDay?: boolean } = {}): string | undefined {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return undefined;

  const instant = new Date(`${value}T${options.endOfDay ? '23:59:59' : '00:00:00'}+05:30`);
  return Number.isNaN(instant.getTime()) ? undefined : instant.toISOString();
}
