'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Textarea } from '@mageride/ui';

import { reverseFee, type ReverseFeeState } from '@/server/finance-actions';

/**
 * The wireframe's "Wallet reversal / adjustment" card (US-14.11) —
 * `POST /v1/admin/drivers/wallet/{driverId}/reverse-fee`.
 *
 * ## One reversal per charge, ever
 *
 * The ledger key is `adjustment:fee_reversal:{driverId}:{vehicleId}:{feeDate}`,
 * composed from the business fact rather than from a header — so a double click
 * collides in the ledger and comes back `replayed: true` with the original entry
 * instead of crediting twice. The card **says so after the fact**: an operator who
 * pressed twice is told the second press did nothing, which is the whole reason
 * that flag is on the wire.
 *
 * ## Three identifiers, because a charge is identified by three things
 *
 * `billing.daily_fee_charges` is keyed per driver, per vehicle, per day (D-13's
 * idempotency), so a reversal has to name all three. The wireframe draws one
 * "Driver" field and a free-text amount; that would identify a person and not a
 * charge, and the platform has no route that takes it.
 *
 * ## The amount box is optional and that is the safe default
 *
 * Empty means the **full charged amount**, which admin-bff reads off the charge —
 * the common case, and the one an operator should not have to look up to get
 * right. A partial reversal is the exception and says so.
 *
 * The reason is required here and required again in admin-bff. A reversal with no
 * recorded reason is one nobody can explain later; this copy exists so an operator
 * is told that before the request, in their own language.
 */

export interface ReverseFeeLabels {
  readonly heading: string;
  readonly note: string;
  readonly driver: string;
  readonly driverHint: string;
  readonly vehicle: string;
  readonly vehicleHint: string;
  readonly feeDate: string;
  readonly feeDateHint: string;
  readonly amount: string;
  readonly amountHint: string;
  readonly reason: string;
  readonly reasonHint: string;
  readonly submit: string;
  readonly working: string;
  readonly audit: string;
  readonly done: string;
  readonly replayed: string;
  readonly balanceAfter: string;
  readonly recorded: string;
}

const INITIAL: ReverseFeeState = {};

export function ReverseFeeCard({
  driverId,
  labels,
}: {
  /** Pre-filled from the URL, so a directory row can aim this card the way a report row aims the suspend card. */
  driverId: string;
  labels: ReverseFeeLabels;
}) {
  const [state, formAction, pending] = useActionState(reverseFee, INITIAL);

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-caption text-on-surface-variant">{labels.note}</p>

      {state.posted ? (
        <div
          role="status"
          className="flex flex-col gap-xxs rounded-md border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          <p>{state.posted.replayed ? labels.replayed : labels.done}</p>
          <p className="text-caption text-on-surface-variant">
            {labels.balanceAfter} {state.posted.balanceAfter}
          </p>
          <p className="text-caption text-on-surface-variant">{labels.recorded}</p>
        </div>
      ) : null}

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-wrap gap-sm">
          <Field
            label={labels.driver}
            hint={labels.driverHint}
            className="min-w-[260px] flex-1"
            {...(state.field === 'driverId' && state.message ? { error: state.message } : {})}
          >
            <Input
              name="driverId"
              defaultValue={driverId}
              maxLength={40}
              autoCapitalize="none"
              spellCheck={false}
            />
          </Field>

          <Field
            label={labels.vehicle}
            hint={labels.vehicleHint}
            className="min-w-[260px] flex-1"
            {...(state.field === 'vehicleId' && state.message ? { error: state.message } : {})}
          >
            <Input name="vehicleId" maxLength={40} autoCapitalize="none" spellCheck={false} />
          </Field>
        </div>

        <div className="flex flex-wrap gap-sm">
          <Field
            label={labels.feeDate}
            hint={labels.feeDateHint}
            className="w-[190px]"
            {...(state.field === 'feeDate' && state.message ? { error: state.message } : {})}
          >
            <Input type="date" name="feeDate" />
          </Field>

          <Field
            label={labels.amount}
            hint={labels.amountHint}
            className="w-[200px]"
            {...(state.field === 'amount' && state.message ? { error: state.message } : {})}
          >
            <Input name="amount" type="number" min="0" step="0.01" inputMode="decimal" />
          </Field>
        </div>

        <Field
          label={labels.reason}
          hint={labels.reasonHint}
          {...(state.field === 'reason' && state.message ? { error: state.message } : {})}
        >
          <Textarea name="reason" maxLength={1000} rows={3} />
        </Field>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        <div className="flex flex-wrap items-center gap-sm">
          <Button
            type="submit"
            size="compact"
            disabled={pending}
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.submit}
          </Button>
          <span className="text-caption text-on-surface-variant">{labels.audit}</span>
        </div>
      </form>
    </section>
  );
}
