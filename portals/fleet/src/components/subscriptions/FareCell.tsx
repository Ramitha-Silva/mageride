'use client';

import { useActionState, useState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import {
  setSubscriberFare,
  type SubscriberActionState,
} from '@/server/subscription-actions';

/**
 * **US-23.7's per-subscriber monthly fare** — the wireframe's "Rs 6,000 ✎".
 *
 * The amount is the row's fact and the pencil is the edit, so the cell reads as
 * an amount until somebody chooses to change it. That is not decoration: a roster
 * of fifteen open number inputs is fifteen ways to change the wrong subscriber's
 * fare by tabbing.
 *
 * The field takes **rupees**, and `fareMinorFrom` in the action is the single
 * conversion to the integer minor units the column holds (C113's rule — money
 * crosses the boundary once). `inputMode="decimal"` rather than
 * `type="number"`, so "6,000" typed with the grouping separator an operator
 * actually writes is accepted rather than silently discarded by the browser.
 *
 * Drawn only when `canSetFare` is true — an unsubscribed row has no live
 * subscription and a Free one has no fare, and `SetFareAsync` answers
 * `409 conflict` for both.
 */

const INITIAL: SubscriberActionState = {};

export interface FareCellLabels {
  readonly edit: string;
  readonly fare: string;
  readonly fareHint: string;
  readonly save: string;
  readonly saving: string;
  readonly cancel: string;
  /** What a Free or unsubscribed row shows in place of an amount. */
  readonly none: string;
}

export function FareCell({
  vehicleId,
  subscriberId,
  fare,
  fareInput,
  editable,
  labels,
}: {
  vehicleId: string;
  subscriberId: string;
  /** The amount as the row shows it, or `null` on a Free subscription. */
  fare: string | null;
  /** The same amount in rupees, for the field's default. */
  fareInput: string;
  editable: boolean;
  labels: FareCellLabels;
}) {
  const [state, formAction, pending] = useActionState(setSubscriberFare, INITIAL);
  const [editing, setEditing] = useState(false);

  if (editing) {
    return (
      <form action={formAction} className="flex flex-col gap-xs">
        <input type="hidden" name="vehicleId" value={vehicleId} />
        <input type="hidden" name="subscriberId" value={subscriberId} />

        <Field
          label={labels.fare}
          hint={labels.fareHint}
          className="w-40"
          {...(state.field === 'fare' && state.message ? { error: state.message } : {})}
        >
          <Input name="fare" inputMode="decimal" autoComplete="off" defaultValue={fareInput} />
        </Field>

        <div className="flex flex-wrap items-center gap-xs">
          <Button type="submit" size="compact" busy={pending} busyLabel={labels.saving}>
            {labels.save}
          </Button>
          <Button type="button" size="compact" variant="ghost" onClick={() => setEditing(false)}>
            {labels.cancel}
          </Button>
        </div>

        {state.message && state.field !== 'fare' ? (
          <p role="alert" className="text-caption text-error">
            {state.message}
          </p>
        ) : null}
      </form>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-xs">
      <span className="whitespace-nowrap tabular-nums">{fare ?? labels.none}</span>

      {editable ? (
        <Button type="button" size="compact" variant="ghost" onClick={() => setEditing(true)}>
          {labels.edit}
        </Button>
      ) : null}

      {state.done ? (
        <span role="status" className="text-caption text-success">
          {state.done}
        </span>
      ) : null}
    </div>
  );
}
