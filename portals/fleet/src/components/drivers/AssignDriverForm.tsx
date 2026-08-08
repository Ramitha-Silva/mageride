'use client';

import { useActionState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import { assignDriver, type DriverActionState } from '@/server/driver-actions';

/**
 * **SCR-FP-005's assign control** (US-13.2, AL-23).
 *
 * ## One field for two arms, because the operator has one of two things
 *
 * `web_fleet.html` draws a single "Assign driver by User ID / phone" box, and
 * `POST …/assignments` takes exactly one of `driverId` and `driverPhone`.
 * `driverReferenceFor` decides which from what was typed — a 26-character ULID is
 * an id, `0771234567` and `+94771234567` are the same number — so the screen does
 * not ask somebody standing in a depot to declare which kind of identifier they
 * are holding.
 *
 * ## Several vehicles, one press
 *
 * US-13.2 assigns a driver to "one or more fleet vehicles" and the route takes
 * one. The checkboxes are the story's own wording; the fan-out is in the action,
 * where a `409` on one vehicle leaves the others assigned and both halves are
 * reported.
 *
 * ## The end date is what makes an assignment temporary
 *
 * Empty is open-ended. A date is AL-23's temporary hire, and it expires with
 * nothing written and nobody pressing anything (US-13.9) — which is why the
 * caption says so beside the field rather than after somebody has forgotten to
 * revoke.
 */

export interface AssignDriverLabels {
  readonly heading: string;
  readonly driver: string;
  readonly driverHint: string;
  readonly vehicles: string;
  readonly vehiclesHint: string;
  readonly from: string;
  readonly fromHint: string;
  readonly to: string;
  readonly toHint: string;
  readonly required: string;
  readonly submit: string;
  readonly submitting: string;
  readonly temporary: string;
  readonly noInvite: string;
  readonly noVehicles: string;
  readonly done: (count: number) => string;
}

export interface AssignableVehicle {
  readonly vehicleId: string;
  readonly label: string;
}

const INITIAL: DriverActionState = {};

export function AssignDriverForm({
  vehicles,
  labels,
}: {
  vehicles: readonly AssignableVehicle[];
  labels: AssignDriverLabels;
}) {
  const [state, formAction, pending] = useActionState(assignDriver, INITIAL);

  if (vehicles.length === 0) {
    return (
      <section className="rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <p className="pt-xs text-body-sm text-on-surface-variant">{labels.noVehicles}</p>
      </section>
    );
  }

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <form action={formAction} className="flex flex-col gap-sm">
        <Field
          label={labels.driver}
          hint={labels.driverHint}
          required
          requiredLabel={labels.required}
          {...(state.field === 'driver' && state.message ? { error: state.message } : {})}
        >
          <Input name="driver" autoComplete="off" spellCheck={false} required />
        </Field>

        <fieldset className="flex flex-col gap-xxs">
          <legend className="text-label text-on-surface-variant">{labels.vehicles}</legend>
          <div className="flex flex-wrap gap-sm py-xxs">
            {vehicles.map((vehicle) => (
              <label
                key={vehicle.vehicleId}
                className="inline-flex items-center gap-xxs rounded-sm border border-outline px-sm py-xxs text-body-sm"
              >
                <input
                  type="checkbox"
                  name="vehicleIds"
                  value={vehicle.vehicleId}
                  className="size-xs accent-primary"
                />
                {vehicle.label}
              </label>
            ))}
          </div>
          <p className="text-caption text-outline-variant">{labels.vehiclesHint}</p>
          {state.field === 'vehicleIds' && state.message ? (
            <p role="alert" className="text-caption text-error">
              {state.message}
            </p>
          ) : null}
        </fieldset>

        <div className="flex flex-col gap-sm md:flex-row">
          <Field label={labels.from} hint={labels.fromHint} className="flex-1">
            <Input type="date" name="from" />
          </Field>

          <Field
            label={labels.to}
            hint={labels.toHint}
            className="flex-1"
            {...(state.field === 'to' && state.message ? { error: state.message } : {})}
          >
            <Input type="date" name="to" />
          </Field>
        </div>

        <p className="text-caption text-on-surface-variant">{labels.temporary}</p>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {state.assigned !== undefined && state.assigned > 0 ? (
          <p role="status" className="text-body-sm text-success">
            {labels.done(state.assigned)}
          </p>
        ) : null}

        {/*
          Each refusal is named with the vehicle it belongs to. An assignment
          fan-out is not a transaction — a driver already holding an open
          assignment on one of the chosen vehicles is a 409 on that vehicle alone
          — so reporting one message for the press would either hide the
          successes or claim the failure for all of them.
        */}
        {state.refused?.map((refusal) => (
          <p key={refusal.vehicleId} role="alert" className="text-body-sm text-error">
            {refusal.message}
          </p>
        ))}

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

      {/*
        The sketch draws an "Invite sent · Resend" row. Nothing on any contract
        invites a driver — `POST …/assignments` answers `404 driver-not-found`
        for a number with no Driver App account — so the screen says how a driver
        comes to exist instead of drawing a control that posts nowhere.
      */}
      <p className="rounded-md bg-surface-variant px-sm py-xs text-caption text-on-surface-variant">
        {labels.noInvite}
      </p>
    </section>
  );
}
