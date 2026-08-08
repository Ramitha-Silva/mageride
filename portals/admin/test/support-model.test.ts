import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  FINANCE_CATEGORIES,
  findTicket,
  ticketHref,
  ticketQueue,
  ticketSearch,
  ticketSelection,
  type TicketRow,
} from '@/api/support';
import {
  lookupLinks,
  queueTotal,
  refundQueueHref,
  ticketDetail,
  ticketRows,
  type RenderContext,
} from '@/components/support/model';
import { createAdminTranslator } from '@/i18n';

import { menuFor } from './support/urd';

/**
 * SCR-AP-005's model, and the one rule on it that is a copy of somebody else's.
 *
 * `FINANCE_CATEGORIES` mirrors `TicketQueues.FinanceCategories` in
 * `backend/src/Support.Api/Domain/SupportVocabulary.cs`, because admin-bff's
 * `TicketRow` drops the `queue` support-svc derives and the wireframe puts a
 * Finance pill on the refund row. That file duplicates the same two strings from
 * subscription-svc and fare-svc **on purpose** — a shared constant would put a
 * category vocabulary in the kernel — so the portal's copy is held to it here
 * rather than trusted, exactly as `audit.test.ts` holds the D-35 vocabulary to
 * `AdminAuditActions.cs`.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const VOCABULARY = join(REPO_ROOT, 'backend/src/Support.Api/Domain/SupportVocabulary.cs');

/** `TicketQueues.FinanceCategories`, read out of the C# rather than typed again. */
function financeCategories(): string[] {
  const source = readFileSync(VOCABULARY, 'utf8');
  const block =
    /FinanceCategories\s*=\s*new HashSet<string>\(StringComparer\.Ordinal\)\s*\{([^}]*)\}/s.exec(
      source,
    );

  if (!block) throw new Error('TicketQueues.FinanceCategories could not be read.');

  return [...block[1]!.matchAll(/"([a-z_]+)"/g)].map((match) => match[1]!);
}

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const TICKET = '0199a1f0-0000-7000-8000-000000004521';
const OTHER = '0199a1f0-0000-7000-8000-000000004519';
const USER = '0199a1f0-0000-7000-8000-000000090431';

function ticket(over: Partial<TicketRow> = {}): TicketRow {
  return {
    ticketId: TICKET,
    userId: USER,
    category: 'wrong_fare',
    status: 'OPEN',
    description: 'Charged Rs 850 but the estimate was Rs 720.',
    createdAt: '2026-06-17T03:10:00Z',
    ...over,
  };
}

describe('which pile a ticket is worked from', () => {
  it('names the same categories support-svc names', () => {
    expect([...FINANCE_CATEGORIES].sort()).toEqual(financeCategories().sort());
  });

  it('routes both money categories to Finance and everything else to Support', () => {
    expect(ticketQueue('daily_fee_refund')).toBe('finance');
    expect(ticketQueue('driver_qr_dispute')).toBe('finance');
    expect(ticketQueue('wrong_fare')).toBe('support');
    expect(ticketQueue(undefined)).toBe('support');
  });

  it('pills the exception and leaves the ordinary ticket unmarked', () => {
    const [dispute] = ticketRows([ticket({ category: 'driver_qr_dispute' })], {}, context);
    const [ordinary] = ticketRows([ticket()], {}, context);

    expect(dispute?.financeQueue?.label).toBe('Finance');
    expect(ordinary?.financeQueue).toBeNull();
  });
});

describe('a queue row', () => {
  it('translates the two categories the platform writes and prints the rest as stored', () => {
    const [known] = ticketRows([ticket({ category: 'daily_fee_refund' })], {}, context);
    const [unknown] = ticketRows([ticket({ category: 'wrong_fare' })], {}, context);

    expect(known?.category).toBe('Daily-fee refund request');
    expect(unknown?.category).toBe('wrong_fare');
  });

  it('links to itself carrying the agent’s filters', () => {
    const selection = { status: 'OPEN' as const, category: 'wrong_fare' };
    const [row] = ticketRows([ticket()], selection, context);

    expect(row?.href).toBe(`/support/tickets?status=OPEN&category=wrong_fare&ticket=${TICKET}`);
  });

  it('marks the one being read', () => {
    const rows = ticketRows([ticket(), ticket({ ticketId: OTHER })], { ticketId: OTHER }, context);

    expect(rows.map((row) => row.selected)).toEqual([false, true]);
  });
});

describe('the thread', () => {
  it('is the raiser’s words and the agent’s, in that order', () => {
    const detail = ticketDetail(
      ticket({ status: 'RESOLVED', response: 'Refunded the peak surcharge.', resolvedAt: '2026-06-17T05:00:00Z' }),
      context,
    );

    expect(detail.thread.map((entry) => entry.body)).toEqual([
      'Charged Rs 850 but the estimate was Rs 720.',
      'Refunded the peak surcharge.',
    ]);
    expect(detail.resolved).toBe(true);
  });

  it('dates a reply only when the platform gave an instant for it', () => {
    // A reply on an unresolved ticket has no timestamp on this payload —
    // `resolvedAt` is the only one — and dating it with that would be a guess.
    const detail = ticketDetail(
      ticket({ status: 'IN_PROGRESS', response: 'Which trip was this?' }),
      context,
    );

    expect(detail.thread[1]?.at).toBeNull();
    expect(detail.resolved).toBe(false);
  });

  it('is empty rather than invented when the row carries no words at all', () => {
    expect(ticketDetail(ticket({ description: '  ' }), context).thread).toEqual([]);
  });
});

describe('the queue read', () => {
  it('sends only the two filters the contract declares', () => {
    expect(ticketSearch({ status: 'OPEN', category: 'wrong_fare', ticketId: TICKET })).toEqual({
      status: 'OPEN',
      category: 'wrong_fare',
      limit: 100,
    });
  });

  it('drops a status admin-bff would answer 400 for', () => {
    expect(ticketSelection({ status: 'CLOSED' })).toEqual({});
  });

  it('finds the selected ticket in the page that was read', () => {
    const rows = [ticket(), ticket({ ticketId: OTHER })];

    expect(findTicket(rows, OTHER)?.ticketId).toBe(OTHER);
    expect(findTicket(rows, '0199a1f0-0000-7000-8000-000000009999')).toBeNull();
    expect(findTicket(rows, undefined)).toBeNull();
  });

  it('counts what one page answered, and an em dash when it failed', () => {
    expect(queueTotal({ items: [ticket()], cursor: null, hasMore: false }, context)).toBe(
      '1 in this queue',
    );
    expect(queueTotal({ items: [ticket()], cursor: 'x', hasMore: true }, context)).toBe(
      '1+ in this queue',
    );
    expect(queueTotal(null, context)).toBe('—');
  });

  it('clears to the bare screen when nothing is filtered', () => {
    expect(ticketHref({})).toBe('/support/tickets');
  });
});

describe('what a Support CSR is offered, and what they are not', () => {
  // Built from URD §2.3 and `AdminMenu.cs`, not from a fixture — so a change to
  // the matrix lands here as a changed expectation.
  const csr = menuFor(['support_csr']);
  const officer = menuFor(['verification_officer']);

  it('links the refund queue for the role whose cell is ◐ raise/recommend', () => {
    // Read opens the queue; Write is what the cell withholds, and admin-bff is
    // what enforces it. The link is the whole of the hand-off — this screen posts
    // nothing to a finance route.
    expect(refundQueueHref(csr)).toBe('/finance/refunds');
  });

  it('draws no refund link for a role whose menu has no refunds item', () => {
    expect(refundQueueHref(officer)).toBeNull();
  });

  it('offers both directories, because a userId does not say which one holds it', () => {
    expect(lookupLinks(csr, USER, t).map((lookup) => lookup.href)).toEqual([
      `/passengers/${USER}`,
      `/drivers/${USER}`,
    ]);
  });

  it('offers none to a role that holds neither directory', () => {
    expect(lookupLinks(officer, USER, t)).toEqual([]);
  });
});
