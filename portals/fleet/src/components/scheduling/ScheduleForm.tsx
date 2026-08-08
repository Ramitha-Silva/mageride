'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import { createSchedule, type ScheduleActionState } from '@/server/schedule-actions';

/**
 * **SCR-FP-008's "+ Schedule ride"** (US-13.11) — a vehicle, a departure and the
 * offset at which the not-started alarm rings in the assigned driver's app.
 *
 * ## Three fields, because the fourth has nothing to offer
 *
 * `createFleetSchedule` takes `{vehicleId, routeId?, departAt, notStartedAlarmMinutes?}`
 * and this form sends three of them. **`routeId` is a `spatial.routes` id and no
 * contract publishes those rows** (`@/api/schedules`), so a picker would have
 * nothing to list and a free-text field would ask an operator to type a UUID. The
 * screen's caption says so; the form does not pretend.
 *
 * ## The departure is a wall clock, and the wall is in Colombo
 *
 * `<input type="datetime-local">` has no time zone on it, and the action that
 * reads the value runs in a container set to UTC. `departAtFrom` resolves it
 * against Asia/Colombo (D-13), which is the difference between the 06:00 from the
 * depot and 11:30. `min` is now in the same wall clock, because a departure in the
 * past is refused by the service — "it would be swept into MISSED by the very next
 * pass and would ring an alarm about a bus that left this morning".
 *
 * ## The alarm has no off switch, here or anywhere
 *
 * `not_started_alarm_minutes` is `NOT NULL` with a 1…120 CHECK, so the wireframe's
 * toggle would have no state to write. What the operator chooses is the offset,
 * and it is chosen once: nothing on any contract changes or cancels a booked
 * departure afterwards.
 */

export interface ScheduleFormLabels {
  readonly heading: string;
  readonly vehicle: string;
  readonly departAt: string;
  readonly departAtHint: string;
  readonly alarm: string;
  readonly alarmHint: string;
  readonly required: string;
  readonly submit: string;
  readonly submitting: string;
  readonly noVehicles: string;
  readonly noRoute: string;
}

export interface SchedulableVehicleOption {
  readonly vehicleId: string;
  readonly label: string;
}

const INITIAL: ScheduleActionState = {};

export function ScheduleForm({
  vehicles,
  minDepartAt,
  defaultAlarmMinutes,
  alarmRange,
  labels,
}: {
  vehicles: readonly SchedulableVehicleOption[];
  /** Now, as a Colombo wall clock — the earliest departure the service accepts. */
  readonly minDepartAt: string;
  readonly defaultAlarmMinutes: number;
  readonly alarmRange: { readonly min: number; readonly max: number };
  labels: ScheduleFormLabels;
}) {
  const [state, formAction, pending] = useActionState(createSchedule, INITIAL);

  if (vehicles.length === 0) {
    return (
      <section
        id="schedule-ride"
        className="rounded-card border border-outline bg-background p-md shadow-card"
      >
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <p className="pt-xs text-body-sm text-on-surface-variant">{labels.noVehicles}</p>
      </section>
    );
  }

  return (
    <section
      id="schedule-ride"
      className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card"
    >
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.vehicle}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...(state.field === 'vehicleId' && state.message ? { error: state.message } : {})}
          >
            <Select name="vehicleId" defaultValue={vehicles[0]?.vehicleId} required>
              {vehicles.map((vehicle) => (
                <option key={vehicle.vehicleId} value={vehicle.vehicleId}>
                  {vehicle.label}
                </option>
              ))}
            </Select>
          </Field>

          <Field
            label={labels.departAt}
            hint={labels.departAtHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...(state.field === 'departAt' && state.message ? { error: state.message } : {})}
          >
            <Input type="datetime-local" name="departAt" min={minDepartAt} required />
          </Field>

          <Field
            label={labels.alarm}
            hint={labels.alarmHint}
            className="md:w-40"
            {...(state.field === 'notStartedAlarmMinutes' && state.message
              ? { error: state.message }
              : {})}
          >
            <Input
              type="number"
              name="notStartedAlarmMinutes"
              inputMode="numeric"
              min={alarmRange.min}
              max={alarmRange.max}
              step={1}
              defaultValue={defaultAlarmMinutes}
            />
          </Field>
        </div>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {state.booked ? (
          <p role="status" className="text-body-sm text-success">
            {state.booked}
          </p>
        ) : null}

        <Button
          type="submit"
          size="compact"
          busy={pending}
          busyLabel={labels.submitting}
          className="self-start"
        >
          {labels.submit}
        </Button>
      </form>

      <p className="text-caption text-on-surface-variant">{labels.noRoute}</p>
    </section>
  );
}
