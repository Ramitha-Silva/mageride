import type { StatusTone } from '@mageride/ui';

import type { Assignment } from '@/api/drivers';
import {
  driversCovering,
  scheduleStatusView,
  SCHEDULE_ALARM_DEFAULT_MINUTES,
  type FleetSchedule,
} from '@/api/schedules';
import type { FleetVehicle } from '@/api/vehicles';
import { formatInstant } from '@/i18n/format';
import type { FleetTranslator, Locale } from '@/i18n';

import { vehicleTypeLabel } from '../vehicles/vehicle-model';

/**
 * SCR-FP-008's view model — three org-scoped reads turned into the wireframe's
 * five columns, on the server, once per render.
 *
 * | column | source |
 * |---|---|
 * | Vehicle | the roster's plate and type, joined on `vehicleId` — plus **whose app rings** |
 * | Route | `FleetSchedule.routeId`, which nothing can turn into a name (see `@/api/schedules`) |
 * | Start | `departAt`, rendered as a **Colombo** instant |
 * | Not-started alarm | `notStartedAlarmMinutes`, and when the alarm actually rang |
 * | Status | `status`, in the sketch's own four pills |
 *
 * ## The driver line is the alarm's recipient, worked out the way the alarm is
 *
 * US-13.11's alarm "rings in the assigned driver's app", and the worker resolves
 * that recipient from the assignment covering the **booked departure**, not the
 * one covering now. {@link driversCovering} is that predicate; this puts its
 * answer on the row, so an operator can see before the departure that a booked
 * bus has nobody assigned over it — the case `ScheduleAlarmWorker` otherwise
 * discovers at alarm time and logs as "there is nobody to tell".
 *
 * It is deliberately **not** a sixth column: the wireframe draws five, and the
 * recipient belongs to the vehicle it is a driver of.
 */

export interface ScheduleRow {
  readonly key: string;
  /** Plate, or a sentence when the roster could not be read. */
  readonly vehicle: string;
  /** The vehicle type, when the roster answered. */
  readonly vehicleType: string | null;
  /** Who the alarm would ring, or that nobody is assigned over this departure. */
  readonly rings: string;
  /** False when nobody is assigned — the row says so in the error tone. */
  readonly ringsSomebody: boolean;
  /** The `spatial.routes` reference, or `null` when the departure carries none. */
  readonly route: string | null;
  readonly start: string;
  readonly alarm: string;
  /** When the alarm actually rang. Present exactly when it has. */
  readonly alarmRang: string | null;
  readonly status: string;
  readonly statusTone: StatusTone;
}

export interface ScheduleRowInputs {
  readonly schedules: readonly FleetSchedule[];
  readonly vehicles: ReadonlyMap<string, FleetVehicle>;
  readonly assignments: readonly Assignment[];
  readonly locale: Locale;
  readonly t: FleetTranslator;
}

export function scheduleRows(inputs: ScheduleRowInputs): ScheduleRow[] {
  const { schedules, vehicles, assignments, locale, t } = inputs;

  return schedules.map((schedule) => {
    const vehicle = vehicles.get(schedule.vehicleId);
    const status = scheduleStatusView(schedule.status);

    const drivers = driversCovering(assignments, schedule.vehicleId, schedule.departAt);
    const named = drivers
      .map((assignment) => assignment.driverName ?? assignment.driverPhone)
      .filter((name): name is string => Boolean(name));

    return {
      key: schedule.scheduleId,
      vehicle: vehicle?.registrationNumber ?? t('fleet.scheduling.unknownVehicle'),
      vehicleType: vehicle ? vehicleTypeLabel(vehicle.vehicleType, t) : null,
      rings:
        drivers.length === 0
          ? t('fleet.scheduling.ringsNobody')
          : t('fleet.scheduling.ringsDriver', {
              // A driver with no name on the assignment is still a recipient, so
              // the count carries the row rather than dropping it.
              driver: named.length > 0 ? named.join(', ') : t('fleet.scheduling.driverUnnamed'),
            }),
      ringsSomebody: drivers.length > 0,
      route: schedule.routeId ?? null,
      start: formatInstant(locale, schedule.departAt) ?? schedule.departAt,
      alarm: t('fleet.scheduling.alarmOffset', {
        minutes: schedule.notStartedAlarmMinutes ?? SCHEDULE_ALARM_DEFAULT_MINUTES,
      }),
      alarmRang: schedule.alarmRaisedAt
        ? t('fleet.scheduling.alarmRang', {
            time: formatInstant(locale, schedule.alarmRaisedAt) ?? schedule.alarmRaisedAt,
          })
        : null,
      status: t(status.labelKey),
      statusTone: status.tone,
    };
  });
}

/**
 * The vehicles a departure can be booked for.
 *
 * **Approved only.** `POST …/schedules` inserts through a statement that checks
 * the vehicle is on this organisation's roster, and a vehicle still waiting on an
 * officer has no driver, no tracker and no service to run — offering it would
 * book a departure nothing could make. Plate order, which is the order fleet-svc
 * answers the roster in.
 */
export interface SchedulableVehicle {
  readonly vehicleId: string;
  readonly label: string;
}

export function schedulableVehicles(
  vehicles: readonly FleetVehicle[],
  t: FleetTranslator,
): SchedulableVehicle[] {
  return vehicles
    .filter((vehicle) => vehicle.status === 'APPROVED')
    .map((vehicle) => ({
      vehicleId: vehicle.vehicleId,
      label: `${vehicle.registrationNumber} · ${vehicleTypeLabel(vehicle.vehicleType, t)}`,
    }));
}
