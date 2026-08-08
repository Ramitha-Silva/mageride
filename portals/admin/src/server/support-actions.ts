'use server';

import { redirect } from 'next/navigation';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { isAdminId } from '@/api/moderation';
import { isTicketStatus, ticketResolvePath, type TicketRow } from '@/api/support';
import { getTranslator } from '@/i18n/server';

/**
 * SCR-AP-005's one decision: answering a ticket and closing it (US-16.3).
 *
 * ## One verb, where the story has two
 *
 * US-16.3 is "respond to support tickets **and** mark them as resolved", and
 * support-svc built both — `POST /v1/internal/support/tickets/{id}/respond` exists
 * precisely so "an agent asking a clarifying question had to resolve the ticket to
 * be heard" stopped being true. admin-bff exposes no route onto it. So this is
 * resolve, the response text is mandatory because a resolution with nothing to
 * read is a status the person who complained has to interpret, and the C107
 * handoff carries the gap.
 *
 * ## Where it returns to
 *
 * The agent's filters, minus the ticket. A resolved ticket has left the pile they
 * are working — an `OPEN` filter will not answer with it again — so keeping the
 * right-hand pane on it would show a row the queue beside it no longer has.
 * `?resolved=` is what lets the queue say what happened after the row is gone,
 * which is the shape SCR-AP-003 uses for a verdict.
 */

export interface ResolveTicketState {
  readonly message?: string;
  /** Set when the failure is about the response box rather than the call. */
  readonly field?: 'response';
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

export async function resolveTicket(
  _state: ResolveTicketState,
  formData: FormData,
): Promise<ResolveTicketState> {
  const t = await getTranslator();

  const ticketId = text(formData, 'ticketId');
  const response = text(formData, 'response');

  if (!isAdminId(ticketId)) return { message: t('admin.error.unexpected') };

  // admin-bff refuses an empty response with a 400 and says why in the field
  // error: "it is shown verbatim to the person who raised the ticket". Saying it
  // here first means the agent reads it in their own language, beside the box.
  if (!response) {
    return { message: t('admin.support.resolve.responseRequired'), field: 'response' };
  }

  try {
    await mutate<TicketRow, { response: string }>({
      method: 'POST',
      path: ticketResolvePath(ticketId),
      body: { response },
      audit: { action: 'TICKET_RESOLVED', entity: 'support_ticket', entityId: ticketId },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  const status = text(formData, 'status');
  const category = text(formData, 'category');

  const query = new URLSearchParams({ resolved: ticketId });
  if (isTicketStatus(status)) query.set('status', status);
  if (category) query.set('category', category.slice(0, 60));

  redirect(`/support/tickets?${query.toString()}`);
}
