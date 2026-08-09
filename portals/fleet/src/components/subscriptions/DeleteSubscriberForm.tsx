'use client';

import { useActionState, useState } from 'react';

import { Button } from '@mageride/ui';

import { deleteSubscriber, type SubscriberActionState } from '@/server/subscription-actions';

/**
 * **AL-25's hard delete** — the owner removing a muted, unsubscribed row
 * (US-23.12, item 17).
 *
 * The row got here because the *passenger* unsubscribed: "an unsubscribed
 * passenger remains visible but muted in the Fleet Portal until the owner deletes
 * that subscriber from the corresponding vehicle". `DeleteSubscriberAsync` refuses
 * an active row with `409 conflict` — "only a passenger can end their own
 * subscription" — so this button appears on a muted row and on no other.
 *
 * It asks once before it deletes. The delete is a hard delete of the grant, and
 * the row it removes is the last trace of a subscription that already ended; there
 * is no undo, and a passenger who wants back has to request again and be accepted
 * (item 17's own sentence). A `<Modal>` would be a heavier answer to the same
 * question in a table cell, so the confirmation is the cell.
 */

const INITIAL: SubscriberActionState = {};

export interface DeleteSubscriberLabels {
  readonly delete: string;
  readonly confirm: string;
  readonly confirming: string;
  readonly cancel: string;
  readonly warning: string;
}

export function DeleteSubscriberForm({
  vehicleId,
  subscriberId,
  passenger,
  labels,
}: {
  vehicleId: string;
  subscriberId: string;
  passenger: string;
  labels: DeleteSubscriberLabels;
}) {
  const [state, formAction, pending] = useActionState(deleteSubscriber, INITIAL);
  const [asking, setAsking] = useState(false);

  if (state.done) {
    return (
      <p role="status" className="text-caption text-success">
        {state.done}
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-xxs">
      {asking ? (
        <form action={formAction} className="flex flex-col gap-xxs">
          <input type="hidden" name="vehicleId" value={vehicleId} />
          <input type="hidden" name="subscriberId" value={subscriberId} />
          <input type="hidden" name="passenger" value={passenger} />

          <p className="text-caption text-on-surface-variant">{labels.warning}</p>

          <div className="flex flex-wrap items-center gap-xs">
            <Button
              type="submit"
              size="compact"
              variant="secondary"
              busy={pending}
              busyLabel={labels.confirming}
              className="text-error"
            >
              {labels.confirm}
            </Button>
            <Button type="button" size="compact" variant="ghost" onClick={() => setAsking(false)}>
              {labels.cancel}
            </Button>
          </div>
        </form>
      ) : (
        <Button
          type="button"
          size="compact"
          variant="ghost"
          className="text-error"
          onClick={() => setAsking(true)}
        >
          {labels.delete}
        </Button>
      )}

      {state.message ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}
    </div>
  );
}
