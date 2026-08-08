import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { MutateOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-005's one decision, as the request it becomes (US-16.3, D-35).
 *
 *  - **The reply is mandatory and is the answer**, not an internal note:
 *    support-svc shows it verbatim to the person who raised the ticket.
 *  - **It returns to the pile the agent was working**, minus the ticket — a
 *    resolved ticket leaves an `OPEN` queue, so the reading pane cannot stay on
 *    it.
 *  - **Nothing here touches money.** The fence is structural: this action's only
 *    path is the ticket's own resolve route.
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const redirect = vi.fn<(url: string) => never>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/navigation', () => ({ redirect: (url: string) => redirect(url) }));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createAdminTranslator('en') }));

const { resolveTicket } = await import('@/server/support-actions');

const TICKET = '0199a1f0-0000-7000-8000-000000004521';

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

async function resolved(values: Record<string, string>) {
  try {
    return await resolveTicket({}, form(values));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('redirect:')) return {};
    throw error;
  }
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200 });
  redirect.mockImplementation((url: string) => {
    throw new Error(`redirect:${url}`);
  });
});

describe('resolving a ticket', () => {
  it('sends the reply and declares the row it writes', async () => {
    await resolved({ ticketId: TICKET, response: 'Refunded the peak surcharge.' });

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        path: `/v1/admin/support/tickets/${TICKET}/resolve`,
        body: { response: 'Refunded the peak surcharge.' },
        audit: { action: 'TICKET_RESOLVED', entity: 'support_ticket', entityId: TICKET },
      }),
    );
  });

  it('will not close a ticket with nothing for the person to read', async () => {
    const state = await resolved({ ticketId: TICKET, response: '   ' });

    expect(mutate).not.toHaveBeenCalled();
    expect(state.field).toBe('response');
  });

  it('refuses a ticket id that is not one', async () => {
    const state = await resolved({ ticketId: 'TK-4521', response: 'Done.' });

    expect(mutate).not.toHaveBeenCalled();
    expect(state.message).toBeTruthy();
  });

  it('returns to the pile that was being worked, without the ticket', async () => {
    await resolved({
      ticketId: TICKET,
      response: 'Done.',
      status: 'OPEN',
      category: 'driver_qr_dispute',
    });

    expect(redirect).toHaveBeenCalledWith(
      `/support/tickets?resolved=${TICKET}&status=OPEN&category=driver_qr_dispute`,
    );
  });

  it('ignores a status nobody could have filtered by', async () => {
    await resolved({ ticketId: TICKET, response: 'Done.', status: 'CLOSED' });

    expect(redirect).toHaveBeenCalledWith(`/support/tickets?resolved=${TICKET}`);
  });

  it('hands a refusal back beside the box rather than throwing the pane away', async () => {
    mutate.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/forbidden',
        title: 'forbidden',
        status: 403,
      }),
    );

    const state = await resolved({ ticketId: TICKET, response: 'Done.' });

    expect(state.message).toBe(createAdminTranslator('en')('admin.error.forbidden'));
    expect(redirect).not.toHaveBeenCalled();
  });
});
