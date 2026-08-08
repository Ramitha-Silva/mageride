'use client';

import { useActionState, useState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import type { ServicePayment } from '@/api/vehicles';
import { setServicePayment, type VehicleActionState } from '@/server/vehicle-actions';

/**
 * **AL-51's "Service payment"** on a vehicle that already exists — the control
 * SCR-FP-004's status table's column reports (US-27.4, US-13.1b).
 *
 * `PUT …/vehicles/{id}/classification` with `modeBBilling`, because AL-51 renamed
 * the label and nothing else: "the API path and the DB column are intentionally
 * unchanged". Everything the operator reads here says Service payment; everything
 * on the wire says `modeBBilling`, and the two meet in this file.
 *
 * `paidAvailable === false` is BR-31.1 biting, and the sentence beside the
 * disabled option is the one `@/api/payout` exports — the same words SCR-FP-002a
 * uses to say what is waiting on the profile.
 */

export interface ServicePaymentLabels {
  readonly heading: string;
  readonly field: string;
  readonly hint: string;
  readonly free: string;
  readonly paid: string;
  readonly fare: string;
  readonly fareHint: string;
  readonly paidBlocked: string | null;
  readonly submit: string;
  readonly submitting: string;
  readonly saved: string;
}

const INITIAL: VehicleActionState = {};

export function ServicePaymentForm({
  vehicleId,
  current,
  currentFareMinor,
  paidAvailable,
  labels,
}: {
  vehicleId: string;
  current: ServicePayment | null;
  currentFareMinor: number | null;
  /** `null` for a caller who cannot read the Owner-only payout profile. */
  paidAvailable: boolean | null;
  labels: ServicePaymentLabels;
}) {
  const [state, formAction, pending] = useActionState(setServicePayment, INITIAL);
  const [choice, setChoice] = useState<string>(current ?? 'free');

  const paidDisabled = paidAvailable === false;

  return (
    <section className="flex flex-col gap-sm rounded-md border border-outline bg-surface p-sm">
      <h3 className="text-body-sm font-semibold">{labels.heading}</h3>

      <form action={formAction} className="flex flex-col gap-sm">
        <input type="hidden" name="vehicleId" value={vehicleId} />

        <div className="flex flex-col gap-sm md:flex-row md:items-start">
          <Field label={labels.field} hint={labels.hint} className="flex-1">
            <Select
              name="modeBBilling"
              value={choice}
              onChange={(event) => setChoice(event.target.value)}
            >
              <option value="free">{labels.free}</option>
              <option value="paid" disabled={paidDisabled}>
                {labels.paid}
              </option>
            </Select>
          </Field>

          {choice === 'paid' ? (
            <Field
              label={labels.fare}
              hint={labels.fareHint}
              className="flex-1"
              {...(state.field === 'fare' && state.message ? { error: state.message } : {})}
            >
              <Input
                name="fare"
                inputMode="decimal"
                autoComplete="off"
                defaultValue={currentFareMinor === null ? '' : String(currentFareMinor / 100)}
              />
            </Field>
          ) : null}
        </div>

        {paidDisabled && labels.paidBlocked ? (
          <p className="rounded-md border border-warning/40 bg-warning/10 px-sm py-xs text-caption">
            {labels.paidBlocked}
          </p>
        ) : null}

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {state.saved ? (
          <p role="status" className="text-body-sm text-success">
            {labels.saved}
          </p>
        ) : null}

        <Button
          type="submit"
          size="compact"
          variant="secondary"
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
