'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  departAtFrom,
  isAlarmMinutes,
  isFutureDeparture,
  FLEET_SCHEDULES,
  SCHEDULE_ALARM_DEFAULT_MINUTES,
  SCHEDULE_ALARM_MAX_MINUTES,
  SCHEDULE_ALARM_MIN_MINUTES,
  type CreateScheduleBody,
  type FleetSchedule,
} from '@/api/schedules';
import { formatInstant } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-008's one write** — book a departure for one of the organisation's
 * vehicles, with the not-started alarm US-13.11 rings in the assigned driver's app.
 *
 * ## The gate is the endpoint's, transcribed
 *
 * `ops.MapPost("/schedules", …).RequireFleetSubRole(FleetRoles.Manager).RequireApprovedFleet()`
 * — so the declaration is `fleet-operations` **with** the approval gate. Note what
 * is *not* here: no `satisfiesFleetRole(session, 'manager')`. `write` on
 * `fleet-operations` is exactly what a Manager holds and a Viewer does not
 * (`PermissionEvaluator` restricts a Viewer to `Read | OwnScope` in every row), so
 * `canMutate` already answers the sub-role the route declares. Adding a second
 * check on the seat would be the portal re-deriving URD §2.1, which is the thing
 * `@/server/access` says never to do.
 *
 * The **read** beside it carries neither gate (`RequireFleetSubRole(Viewer)`, on a
 * group with no approval gate), which is why the manifest leaves `/scheduling`
 * open to a pending organisation and it is this form that is replaced by a
 * sentence — the same asymmetry SCR-FP-006 has.
 *
 * ## Three refusals are answered on the field, not by the service
 *
 * A departure in the past, an alarm outside 1…120 and a missing vehicle are all
 * `400 validation-failed` from `ScheduleService`. They are checked here as well so
 * the operator is told *which* field, in their own language, without a round trip
 * — the service's clock stays the authority on whether 06:00 has passed.
 *
 * The fourth is the service's alone: `ux_fleet_schedules_slot` answers a duplicate
 * departure with a conflict, and "two managers entering the 06:10 from the depot
 * minutes apart are two genuinely different requests, so an Idempotency-Key does
 * not catch it and the index does". That one gets a sentence of its own, because
 * the shared `conflict` copy cannot say which slot is taken.
 */

export interface ScheduleActionState {
  readonly message?: string;
  readonly field?: 'vehicleId' | 'departAt' | 'notStartedAlarmMinutes';
  /**
   * The confirmation, **already written**: "The 06:00 on 18 Jun is booked…".
   *
   * A finished sentence rather than a value the form interpolates, because a
   * client component cannot be handed a function to build one with — React
   * refuses to serialise a function across the server boundary — and it should
   * not be handed a translator either. The action runs on the server, where the
   * locale, the translator and the Colombo formatter already are.
   */
  readonly booked?: string;
}

/** `fleet-operations` is URD §2.3's "Fleet management — vehicles, drivers, scheduling". */
const REQUIRES = { area: 'fleet-operations', requiresApprovedOrg: true } as const;

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/** `POST /v1/fleets/{id}/schedules` — US-13.11. */
export async function createSchedule(
  _state: ScheduleActionState,
  formData: FormData,
): Promise<ScheduleActionState> {
  const t = await getTranslator();

  const vehicleId = text(formData, 'vehicleId');
  if (!vehicleId) {
    return { message: t('fleet.scheduling.error.vehicleRequired'), field: 'vehicleId' };
  }

  // The wall clock the operator typed, resolved against Asia/Colombo rather than
  // against this container's UTC. See `departAtFrom`.
  const departAt = departAtFrom(text(formData, 'departAt'));
  if (!departAt) {
    return { message: t('fleet.scheduling.error.departAtInvalid'), field: 'departAt' };
  }
  if (!isFutureDeparture(departAt)) {
    return { message: t('fleet.scheduling.error.departAtPast'), field: 'departAt' };
  }

  const typed = text(formData, 'notStartedAlarmMinutes');
  const minutes = typed === '' ? SCHEDULE_ALARM_DEFAULT_MINUTES : Number(typed);
  if (!isAlarmMinutes(minutes)) {
    return {
      message: t('fleet.scheduling.error.alarmRange', {
        min: SCHEDULE_ALARM_MIN_MINUTES,
        max: SCHEDULE_ALARM_MAX_MINUTES,
      }),
      field: 'notStartedAlarmMinutes',
    };
  }

  // `routeId` is deliberately absent — nothing publishes this organisation's
  // routes, so there is nothing to pick from (`ROUTE_PICKER_UNAVAILABLE`).
  const body: CreateScheduleBody = { vehicleId, departAt, notStartedAlarmMinutes: minutes };

  try {
    await mutate<FleetSchedule, CreateScheduleBody>({
      method: 'POST',
      org: FLEET_SCHEDULES,
      body,
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;

    if (error.code === 'conflict') {
      return { message: t('fleet.scheduling.error.slotTaken'), field: 'departAt' };
    }
    return {
      message: t(error.messageKey),
      ...(error.code === 'vehicle-not-found' ? { field: 'vehicleId' as const } : {}),
    };
  }

  revalidatePath('/scheduling');

  return {
    booked: t('fleet.scheduling.book.done', {
      departAt: formatInstant(await getLocale(), departAt) ?? departAt,
      minutes,
    }),
  };
}
