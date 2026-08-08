/**
 * SCR-AP-005's wire shapes, its two paths, and the state the screen keeps in its
 * URL.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` —
 * `listSupportTicketQueue` and `resolveSupportTicket`, which are the whole of
 * `/v1/admin/support/**`. support-svc (C053) owns the rows behind them; admin-bff
 * is the RBAC-gated, audited front door that forwards.
 *
 * ## The queue is the detail
 *
 * **There is no `GET /v1/admin/support/tickets/{ticketId}`.** support-svc built
 * one for this caller (`GET /v1/internal/support/tickets/{ticketId}`, "one ticket
 * with its whole thread") and admin-bff exposes no route onto it, so the ticket an
 * agent is reading is the row out of the page they are reading it from —
 * {@link findTicket}. That is why the selection travels as `?ticket=` on the queue
 * screen rather than as a path segment: a URL that promised a ticket the portal
 * can only find by paging a queue would be a promise it cannot keep. Recorded as a
 * micro-change-set in the C107 handoff.
 *
 * ## What a thread is here
 *
 * `TicketRow` carries the raiser's `description` and the agent's latest
 * `response`, and nothing else: support-svc's own `TicketRow` carries `thread`
 * (every status transition and every reply, oldest first), `screenshotUrl`,
 * `tripId` and `queue`, and admin-bff's mapping keeps none of them. So the thread
 * this screen draws is two entries where the platform holds more, and the
 * evidence attached to an AL-47 driver-QR dispute does not reach this surface at
 * all. Both are in the handoff; neither is worked around here, because a portal
 * that invented a thread would be a portal claiming to show what an agent has
 * not seen.
 *
 * ## Resolve is the only decision
 *
 * US-16.3 is two verbs — "respond … and mark them as resolved" — and support-svc
 * built both (`…/respond` replies without closing). admin-bff has a route for the
 * second only, which its own file notes, so the wireframe's **Reply** button has
 * nothing to call and is not drawn.
 */

import { isAdminId } from './moderation';
import type { CursorPage } from './types';

export type { CursorPage };

/** `GET /v1/admin/support/tickets` · `POST …/{ticketId}/resolve`. */
export const SUPPORT_TICKETS_PATH = '/v1/admin/support/tickets';

/** Cursor pagination carries no total, so the queue count is what one page answered. */
export const QUEUE_PAGE_SIZE = 100;

export function ticketResolvePath(ticketId: string): string {
  return `${SUPPORT_TICKETS_PATH}/${ticketId}/resolve`;
}

/** `support.tickets.status` — `ck_tickets_status` (migration 1303) exactly. */
export const TICKET_STATUSES = ['OPEN', 'IN_PROGRESS', 'RESOLVED'] as const;

export type TicketStatus = (typeof TICKET_STATUSES)[number];

export function isTicketStatus(value: string | undefined): value is TicketStatus {
  return value !== undefined && (TICKET_STATUSES as readonly string[]).includes(value);
}

/** Which back-office pile a ticket is worked from. Derived from its category, never stored. */
export type TicketQueue = 'support' | 'finance';

/**
 * The categories that belong to Finance, mirroring
 * `MageRide.Support.Domain.TicketQueues.FinanceCategories`
 * (`backend/src/Support.Api/Domain/SupportVocabulary.cs`).
 *
 * **A fourth copy of two strings, and it is the same deliberate duplication the
 * other three are.** That file says so in as many words — subscription-svc's
 * `RefundRequestRepository.Category` and fare-svc's
 * `SupportTicketRepository.DriverQrCategory` spell them out rather than sharing a
 * constant, because `support.tickets.category` carries no CHECK and a kernel-wide
 * category vocabulary is a thing every bounded context would extend.
 *
 * The portal needs it because admin-bff's `TicketRow` **drops `queue`** on the way
 * through, and the wireframe puts a Finance pill on the refund-request row: a
 * CSR has to be able to see, without opening it, that a ticket ends in money
 * moving and is therefore not theirs to settle. `test/support-model.test.ts`
 * parses the C# and fails if the sets drift.
 */
export const FINANCE_CATEGORIES: readonly string[] = ['daily_fee_refund', 'driver_qr_dispute'];

/** `TicketQueues.For` — the same pure function over the same two strings. */
export function ticketQueue(category: string | undefined): TicketQueue {
  return category !== undefined && FINANCE_CATEGORIES.includes(category) ? 'finance' : 'support';
}

/** `TicketRow` — one `support.tickets` row as admin-bff answers it. */
export interface TicketRow {
  readonly ticketId: string;
  /** The account that raised it. A passenger's or a driver's — the row does not say which. */
  readonly userId: string;
  /** Free text: `wrong_fare`, `daily_fee_refund`, `driver_qr_dispute`, … (no CHECK). */
  readonly category: string;
  readonly status: TicketStatus;
  readonly description?: string;
  /** The agent's latest reply. Shown verbatim to the person who raised it. */
  readonly response?: string;
  readonly createdAt: string;
  readonly resolvedAt?: string;
}

/** What SCR-AP-005 is currently showing, resolved from the URL. */
export interface TicketSelection {
  readonly status?: TicketStatus;
  /** Free text — the contract's own `category` filter, capped at its `maxLength`. */
  readonly category?: string;
  /** The ticket the right-hand pane is reading. */
  readonly ticketId?: string;
  /** A ticket just resolved, so the queue can say so after the row has left it. */
  readonly resolved?: string;
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

/**
 * The selection, from the page's own search parameters.
 *
 * An unrecognised status is dropped rather than forwarded: it is a closed enum
 * admin-bff answers `400` for, and a pasted URL would otherwise greet an agent
 * with a validation error about a control they never touched. SCR-AP-003's
 * `queueSelection` makes the same call for the same reason.
 */
export function ticketSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): TicketSelection {
  const status = first(params.status);
  const category = first(params.category)?.trim();
  const ticketId = first(params.ticket);
  const resolved = first(params.resolved);

  return {
    ...(isTicketStatus(status) ? { status } : {}),
    ...(category ? { category: category.slice(0, 60) } : {}),
    ...(isAdminId(ticketId) ? { ticketId } : {}),
    ...(isAdminId(resolved) ? { resolved } : {}),
  };
}

/** The selection as admin-bff's query. The pane selection is the portal's and does not travel. */
export function ticketSearch(selection: TicketSelection): Record<string, string | number> {
  return {
    ...(selection.status ? { status: selection.status } : {}),
    ...(selection.category ? { category: selection.category } : {}),
    limit: QUEUE_PAGE_SIZE,
  };
}

/** The selection as a portal query string, for the queue links. */
export function ticketQuery(selection: TicketSelection): Record<string, string> {
  return {
    ...(selection.status ? { status: selection.status } : {}),
    ...(selection.category ? { category: selection.category } : {}),
    ...(selection.ticketId ? { ticket: selection.ticketId } : {}),
  };
}

/** `/support/tickets` with this selection on it — the filter and the pane are the URL. */
export function ticketHref(
  selection: TicketSelection,
  overrides: Partial<TicketSelection> = {},
): string {
  const query = new URLSearchParams(ticketQuery({ ...selection, ...overrides }));
  const search = query.toString();
  return search ? `/support/tickets?${search}` : '/support/tickets';
}

/** The selected row out of the page that was read — see this file's note on the missing detail route. */
export function findTicket(
  rows: readonly TicketRow[],
  ticketId: string | undefined,
): TicketRow | null {
  if (!ticketId) return null;
  return rows.find((row) => row.ticketId === ticketId) ?? null;
}
