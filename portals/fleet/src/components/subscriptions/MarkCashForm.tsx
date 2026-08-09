'use client';

import { useActionState, useState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import {
  markCashReceived,
  type SubscriberActionState,
} from '@/server/subscription-actions';

/**
 * **US-23.6's "Mark received"** — the owner recording a cash payment.
 *
 * "As a passenger paying cash, I hand it to whoever collects for the fleet; **only
 * the fleet Owner can mark it received** in the web portal. Once marked received,
 * the passenger's subscription card shows **Paid**." There is no gateway leg on
 * this rail at all: this button *is* the payment record, which is why it opens a
 * small amount field instead of firing on the first click.
 *
 * The amount defaults to the subscriber's own monthly fare and is editable,
 * because `MarkCashAsync` takes an `amountMinor` of its own and a passenger who
 * hands over a part payment has handed over a part payment. The **month** is not
 * offered: the service defaults `periodMonth` to the subscription's own due month,
 * and a portal that computed one from a browser clock would settle the wrong month
 * either side of a Colombo month boundary.
 */

const INITIAL: SubscriberActionState = {};

export interface MarkCashLabels {
  readonly open: string;
  readonly amount: string;
  readonly amountHint: string;
  readonly submit: string;
  readonly submitting: string;
  readonly cancel: string;
}

export function MarkCashForm({
  vehicleId,
  subscriberId,
  defaultAmount,
  labels,
}: {
  vehicleId: string;
  subscriberId: string;
  /** The subscriber's monthly fare in rupees — what is normally handed over. */
  defaultAmount: string;
  labels: MarkCashLabels;
}) {
  const [state, formAction, pending] = useActionState(markCashReceived, INITIAL);
  const [open, setOpen] = useState(false);

  if (state.done) {
    return (
      <p role="status" className="text-caption text-success">
        {state.done}
      </p>
    );
  }

  if (!open) {
    return (
      <div className="flex flex-col gap-xxs">
        <Button type="button" size="compact" variant="secondary" onClick={() => setOpen(true)}>
          {labels.open}
        </Button>
        {state.message ? (
          <p role="alert" className="text-caption text-error">
            {state.message}
          </p>
        ) : null}
      </div>
    );
  }

  return (
    <form action={formAction} className="flex flex-col gap-xs">
      <input type="hidden" name="vehicleId" value={vehicleId} />
      <input type="hidden" name="subscriberId" value={subscriberId} />

      <Field
        label={labels.amount}
        hint={labels.amountHint}
        className="w-40"
        {...(state.field === 'amount' && state.message ? { error: state.message } : {})}
      >
        <Input name="amount" inputMode="decimal" autoComplete="off" defaultValue={defaultAmount} />
      </Field>

      <div className="flex flex-wrap items-center gap-xs">
        <Button type="submit" size="compact" busy={pending} busyLabel={labels.submitting}>
          {labels.submit}
        </Button>
        <Button type="button" size="compact" variant="ghost" onClick={() => setOpen(false)}>
          {labels.cancel}
        </Button>
      </div>

      {state.message && state.field !== 'amount' ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}
    </form>
  );
}
