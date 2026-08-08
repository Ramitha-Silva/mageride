'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import { bindTracker, type TrackerActionState } from '@/server/tracker-actions';

/**
 * **SCR-FP-006's "+ Bind tracker"** (US-13.12, T-02).
 *
 * ## The auto-session box is the reason the hardware is fitted
 *
 * `autoStartSession` arms tracker-driven journey auto-start (AL-32, T-11): "a bus
 * broadcasts irrespective of any driver app", and its journey begins and ends with
 * the ignition rather than with somebody remembering to press Start. That is what
 * `web_fleet.html`'s "auto-session config" pill means, so the control is on by
 * default and the caption says what it does.
 *
 * ## The IMEI is normalised, not validated into shape
 *
 * The sticker is grouped or hyphenated; separators are stripped and the fifteen
 * digits underneath are what is sent. Anything that does not reduce to fifteen
 * digits is refused on the field, because a guessed IMEI binds the wrong device —
 * and a *duplicate* one is T-08's anti-clone hold, which quarantines **both**
 * bindings and is the one refusal this form shows on the IMEI field itself.
 */

export interface BindTrackerLabels {
  readonly heading: string;
  readonly imei: string;
  readonly imeiHint: string;
  readonly vehicle: string;
  readonly autoStart: string;
  readonly autoStartHint: string;
  readonly required: string;
  readonly submit: string;
  readonly submitting: string;
  readonly noVehicles: string;
}

export interface BindableVehicle {
  readonly vehicleId: string;
  readonly label: string;
}

const INITIAL: TrackerActionState = {};

export function BindTrackerForm({
  vehicles,
  labels,
}: {
  vehicles: readonly BindableVehicle[];
  labels: BindTrackerLabels;
}) {
  const [state, formAction, pending] = useActionState(bindTracker, INITIAL);

  if (vehicles.length === 0) {
    return (
      <section className="rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <p className="pt-xs text-body-sm text-on-surface-variant">{labels.noVehicles}</p>
      </section>
    );
  }

  return (
    <section className="flex flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.imei}
            hint={labels.imeiHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...(state.field === 'imei' && state.message ? { error: state.message } : {})}
          >
            <Input name="imei" inputMode="numeric" autoComplete="off" spellCheck={false} required />
          </Field>

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
        </div>

        <div className="flex flex-col gap-xxs">
          <label className="flex items-center gap-xs text-body-sm">
            <input
              type="checkbox"
              name="autoStartSession"
              defaultChecked
              className="size-xs accent-primary"
            />
            {labels.autoStart}
          </label>
          <p className="text-caption text-on-surface-variant">{labels.autoStartHint}</p>
        </div>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {/* Composed by the action — see `TrackerActionState.bound`. Δ C115. */}
        {state.bound ? (
          <p role="status" className="text-body-sm text-success">
            {state.bound}
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
    </section>
  );
}
