'use client';

import { useActionState } from 'react';

import { Button, Field, Textarea } from '@mageride/ui';

import { resolveTicket, type ResolveTicketState } from '@/server/support-actions';

/**
 * The one decision SCR-AP-005 can take: answer the ticket and close it (US-16.3).
 *
 * ## The box is the answer, not a note
 *
 * `resolve` sends `response`, and support-svc shows it **verbatim** to the person
 * who raised the ticket at `GET /v1/support/tickets/{userId}/{ticketId}`. So the
 * label says so: an agent writing "closed, no action" into a box they think is
 * internal is the failure this copy exists to prevent.
 *
 * ## Why there is no Reply button
 *
 * The wireframe draws one, and support-svc has the route for it
 * (`…/{id}/respond` — built, in its own words, because "an agent asking a
 * clarifying question had to resolve the ticket to be heard"). admin-bff exposes
 * no route onto it, and a button that cannot call anything is worse than its
 * absence. C107 handoff.
 *
 * The agent's filters travel on hidden fields so the action can return them to the
 * pile they were working: a resolved ticket leaves an `OPEN` queue, so the pane
 * cannot stay on it.
 */

export interface ResolveTicketLabels {
  readonly response: string;
  readonly responseHint: string;
  readonly resolve: string;
  readonly working: string;
  readonly audit: string;
}

const INITIAL: ResolveTicketState = {};

export function ResolveTicketForm({
  ticketId,
  status,
  category,
  labels,
}: {
  ticketId: string;
  /** The queue filters, so the verdict returns to the pile that was being worked. */
  status: string;
  category: string;
  labels: ResolveTicketLabels;
}) {
  const [state, formAction, pending] = useActionState(resolveTicket, INITIAL);

  return (
    <form action={formAction} className="flex flex-col gap-sm">
      <input type="hidden" name="ticketId" value={ticketId} />
      <input type="hidden" name="status" value={status} />
      <input type="hidden" name="category" value={category} />

      <Field
        label={labels.response}
        hint={labels.responseHint}
        {...(state.field === 'response' && state.message ? { error: state.message } : {})}
      >
        <Textarea name="response" maxLength={4000} rows={4} />
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
          {labels.resolve}
        </Button>
        <span className="text-caption text-on-surface-variant">{labels.audit}</span>
      </div>
    </form>
  );
}
