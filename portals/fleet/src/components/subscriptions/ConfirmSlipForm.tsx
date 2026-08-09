'use client';

import { useActionState } from 'react';

import { Button } from '@mageride/ui';

import { confirmTransfer, type SubscriberActionState } from '@/server/subscription-actions';

/**
 * **US-23.4/16f's Confirm** — the owner verifying a passenger's transfer slip.
 *
 * The passenger paid by online bank transfer and attached a screenshot; the
 * payment has been `pending_verification` since, and confirming it is the owner
 * saying the money reached their account. `ConfirmAsync` refuses any other status
 * — "only a slip the passenger has uploaded can be confirmed" — so the button is
 * drawn for that status alone.
 *
 * **There is no Reject.** No route sets a payment back or marks it bad; a slip
 * that does not check out is simply left unconfirmed and the month stays open,
 * which is the platform's shape rather than an omission on this screen.
 *
 * "View slip" beside it is a plain anchor to `SubscriptionPaymentRow.slipUrl` —
 * subscription-svc's HMAC-signed, expiring link, whose route is `security: []`
 * because "the signature is the credential … an access token in a query string is
 * an access token in every proxy log on the way". The browser fetches it directly,
 * exactly as it does a bulk import's error report.
 */

const INITIAL: SubscriberActionState = {};

export interface ConfirmSlipLabels {
  readonly submit: string;
  readonly submitting: string;
  readonly viewSlip: string;
}

export function ConfirmSlipForm({
  paymentId,
  slipUrl,
  labels,
}: {
  paymentId: string;
  slipUrl: string | null;
  labels: ConfirmSlipLabels;
}) {
  const [state, formAction, pending] = useActionState(confirmTransfer, INITIAL);

  if (state.done) {
    return (
      <p role="status" className="text-caption text-success">
        {state.done}
      </p>
    );
  }

  return (
    <form action={formAction} className="flex flex-col gap-xxs">
      <input type="hidden" name="paymentId" value={paymentId} />

      <div className="flex flex-wrap items-center gap-xs">
        <Button type="submit" size="compact" busy={pending} busyLabel={labels.submitting}>
          {labels.submit}
        </Button>

        {slipUrl ? (
          <a
            href={slipUrl}
            target="_blank"
            rel="noreferrer"
            className="text-caption text-primary underline underline-offset-2"
          >
            {labels.viewSlip}
          </a>
        ) : null}
      </div>

      {state.message ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}
    </form>
  );
}
