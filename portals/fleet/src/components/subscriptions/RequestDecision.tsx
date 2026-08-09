'use client';

import { useActionState, useState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import {
  acceptRequest,
  rejectRequest,
  type RequestActionState,
} from '@/server/subscription-actions';

/**
 * **Item 15's Accept / Reject**, on one row of the incoming-request queue
 * (US-23.1).
 *
 * Two forms rather than one with two submit buttons, because they are two
 * different writes with two different consequences and each needs its own pending
 * state: Accept "grants tracking access and starts the subscription/billing
 * cycle" and Reject is terminal. A shared `useActionState` would leave one button
 * spinning while the other was pressed.
 *
 * The reason field belongs to Reject alone — `fleet.yaml` gives the reject body an
 * optional `reason` (`maxLength: 500`) and the accept body nothing at all — and it
 * is revealed by pressing Reject rather than sitting open on every row: a queue of
 * fifteen requests with fifteen open text boxes is a queue nobody reads.
 *
 * The passenger's name travels in a hidden field so the action can compose the
 * confirmation sentence on the server. It is a label, never an identifier — the
 * request id is what the route is addressed by.
 */

const INITIAL: RequestActionState = {};

export interface RequestDecisionLabels {
  readonly accept: string;
  readonly accepting: string;
  readonly reject: string;
  readonly rejecting: string;
  readonly confirmReject: string;
  readonly reason: string;
  readonly reasonHint: string;
  readonly cancel: string;
}

export function RequestDecision({
  vehicleId,
  requestId,
  passenger,
  reasonMaxLength,
  labels,
}: {
  vehicleId: string;
  requestId: string;
  passenger: string;
  reasonMaxLength: number;
  labels: RequestDecisionLabels;
}) {
  const [acceptState, acceptAction, accepting] = useActionState(acceptRequest, INITIAL);
  const [rejectState, rejectAction, rejectPending] = useActionState(rejectRequest, INITIAL);
  const [askingReason, setAskingReason] = useState(false);

  // Whichever of the two spoke. Both start empty, so an untouched row shows
  // nothing and a decided one shows the decision that was actually made.
  const accepted = Boolean(acceptState.message ?? acceptState.done);
  const state = accepted ? acceptState : rejectState;

  return (
    <div className="flex flex-col gap-xs">
      <div className="flex flex-wrap items-center gap-xs">
        <form action={acceptAction}>
          <input type="hidden" name="vehicleId" value={vehicleId} />
          <input type="hidden" name="requestId" value={requestId} />
          <input type="hidden" name="passenger" value={passenger} />
          <Button type="submit" size="compact" busy={accepting} busyLabel={labels.accepting}>
            {labels.accept}
          </Button>
        </form>

        {askingReason ? null : (
          <Button
            type="button"
            size="compact"
            variant="ghost"
            className="text-error"
            onClick={() => setAskingReason(true)}
          >
            {labels.reject}
          </Button>
        )}
      </div>

      {askingReason ? (
        <form action={rejectAction} className="flex flex-col gap-xs">
          <input type="hidden" name="vehicleId" value={vehicleId} />
          <input type="hidden" name="requestId" value={requestId} />
          <input type="hidden" name="passenger" value={passenger} />

          <Field label={labels.reason} hint={labels.reasonHint} className="w-56">
            <Input name="reason" maxLength={reasonMaxLength} autoComplete="off" />
          </Field>

          <div className="flex flex-wrap items-center gap-xs">
            <Button
              type="submit"
              size="compact"
              variant="secondary"
              busy={rejectPending}
              busyLabel={labels.rejecting}
            >
              {labels.confirmReject}
            </Button>
            <Button
              type="button"
              size="compact"
              variant="ghost"
              onClick={() => setAskingReason(false)}
            >
              {labels.cancel}
            </Button>
          </div>
        </form>
      ) : null}

      {state.message ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}

      {state.done ? (
        <p role="status" className="text-caption text-success">
          {state.done}
        </p>
      ) : null}
    </div>
  );
}
