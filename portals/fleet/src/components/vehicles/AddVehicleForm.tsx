'use client';

import { useRouter } from 'next/navigation';
import { useActionState, useEffect, useState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import type { FleetMode } from '@/api/vehicles';
import { addVehicle, type VehicleActionState } from '@/server/vehicle-actions';

/**
 * **SCR-FP-004's "Add vehicle" card** (US-13.1, US-13.1b).
 *
 * ## The Service payment pair appears when the vehicle is Mode B, and not before
 *
 * `registry.vehicles.mode_b_billing` is NULL for a Mode A vehicle by design and
 * `ClassificationService` answers `400` for one, so the two controls are mounted
 * on the mode rather than disabled by it: a greyed "Service payment" beside a bus
 * would imply a bus could have one. The fare field is the same rule one level
 * down — it belongs to `paid` and appears with it.
 *
 * ## "Paid" is disabled with a sentence, not hidden
 *
 * BR-31.1 refuses `paid` until the organisation's payout profile is verified, and
 * that *is* a state that changes — an officer approves the profile and the option
 * becomes available. So it is drawn, disabled and explained, which is the
 * opposite of the Owner-seat case on SCR-FP-002 where the operation does not
 * exist at all. The sentence is `PAID_SERVICE_PAYMENT_BLOCKED_KEY`'s, resolved by
 * the page from `@/api/payout` — one rule, one sentence, stated where it bites.
 *
 * ## On success the URL takes over
 *
 * A document is attached to a vehicle, and the vehicle did not exist a moment
 * ago. Rather than hold the new id in this component and hand it sideways to the
 * slots, the form navigates to `?vehicle={the new id}` — the page reads the
 * roster and that vehicle's four slots on the server, and a reload, a bookmark
 * and a back button all land on the same screen.
 */

export interface AddVehicleLabels {
  readonly heading: string;
  readonly plate: string;
  readonly plateHint: string;
  readonly type: string;
  readonly mode: string;
  readonly noTrain: string;
  readonly servicePayment: string;
  readonly servicePaymentHint: string;
  readonly free: string;
  readonly paid: string;
  readonly fare: string;
  readonly fareHint: string;
  readonly paidBlocked: string | null;
  readonly required: string;
  readonly submit: string;
  readonly submitting: string;
  /** `{plate}` already substituted by the page. */
  readonly added: (plate: string) => string;
}

export interface VehicleOption {
  readonly value: string;
  readonly label: string;
}

const INITIAL: VehicleActionState = {};

export function AddVehicleForm({
  types,
  modes,
  paidAvailable,
  labels,
}: {
  types: readonly VehicleOption[];
  modes: readonly VehicleOption[];
  /** Whether BR-31.1 currently allows "Paid". `null` when the caller cannot read the profile. */
  paidAvailable: boolean | null;
  labels: AddVehicleLabels;
}) {
  const [state, formAction, pending] = useActionState(addVehicle, INITIAL);
  const [mode, setMode] = useState<FleetMode>('A');
  const [servicePayment, setServicePayment] = useState('');
  const router = useRouter();

  const created = state.vehicleId;
  useEffect(() => {
    if (created) router.replace(`/vehicles?vehicle=${created}`);
  }, [created, router]);

  // `null` is a Manager, who cannot read the Owner-only payout profile. The
  // option stays enabled and fleet-svc's 409 is what refuses it — offering less
  // than the platform allows on a fact this session cannot check would be the
  // portal inventing a refusal.
  const paidDisabled = paidAvailable === false;

  return (
    <section className="flex flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.plate}
            hint={labels.plateHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...(state.field === 'registrationNumber' && state.message
              ? { error: state.message }
              : {})}
          >
            <Input
              name="registrationNumber"
              maxLength={32}
              autoCapitalize="characters"
              spellCheck={false}
              required
            />
          </Field>

          <Field
            label={labels.type}
            hint={labels.noTrain}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...(state.field === 'vehicleType' && state.message ? { error: state.message } : {})}
          >
            <Select name="vehicleType" defaultValue={types[0]?.value} required>
              {types.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Select>
          </Field>
        </div>

        <Field
          label={labels.mode}
          required
          requiredLabel={labels.required}
          {...(state.field === 'mode' && state.message ? { error: state.message } : {})}
        >
          <Select
            name="mode"
            value={mode}
            onChange={(event) => setMode(event.target.value as FleetMode)}
            required
          >
            {modes.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
        </Field>

        {mode === 'B' ? (
          <div className="flex flex-col gap-sm md:flex-row">
            <Field
              label={labels.servicePayment}
              hint={labels.servicePaymentHint}
              className="flex-1"
            >
              <Select
                name="modeBBilling"
                value={servicePayment}
                onChange={(event) => setServicePayment(event.target.value)}
              >
                <option value="free">{labels.free}</option>
                <option value="paid" disabled={paidDisabled}>
                  {labels.paid}
                </option>
              </Select>
            </Field>

            {servicePayment === 'paid' ? (
              <Field
                label={labels.fare}
                hint={labels.fareHint}
                className="flex-1"
                {...(state.field === 'fare' && state.message ? { error: state.message } : {})}
              >
                <Input name="fare" inputMode="decimal" autoComplete="off" />
              </Field>
            ) : null}
          </div>
        ) : null}

        {mode === 'B' && paidDisabled && labels.paidBlocked ? (
          <p className="rounded-md border border-warning/40 bg-warning/10 px-sm py-xs text-caption">
            {labels.paidBlocked}
          </p>
        ) : null}

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {state.added ? (
          <p role="status" className="text-body-sm text-success">
            {labels.added(state.added)}
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
