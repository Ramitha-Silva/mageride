import type { StatusTone } from '@mageride/ui';

import {
  ticketHref,
  ticketQueue,
  type CursorPage,
  type TicketRow,
  type TicketSelection,
  type TicketStatus,
} from '@/api/support';
import type { AdminMenuGroup } from '@/api/types';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import { formatCount, formatDateTime } from '@/i18n/format';
import { permittedItems } from '@/server/access';

/**
 * SCR-AP-005's view model: the vocabulary and the arithmetic, apart from the
 * markup.
 *
 * ## Two vocabularies, and one of them is deliberately incomplete
 *
 * A ticket `status` is a closed enum (`ck_tickets_status`, migration 1303) and is
 * translated. A `category` is **free text** — `support.tickets.category` carries
 * no CHECK, clients pick from whatever the FAQ surface publishes, and two of the
 * values are written by services rather than by people. So only the two the
 * platform names in code are translated (`daily_fee_refund` from subscription-svc,
 * `driver_qr_dispute` from fare-svc); everything else renders as the stored key.
 *
 * That is the choice C106 made for an unrecognised field key and for the same
 * reason: a raw `wrong_fare` in the column is ugly, obviously a key, and gets
 * reported — an empty cell is an agent working a ticket without knowing what it
 * is about.
 */

export interface PillView {
  readonly tone: StatusTone;
  readonly label: string;
}

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

const STATUS_LABELS: Readonly<Record<TicketStatus, AdminMessageKey>> = {
  OPEN: 'admin.support.status.open',
  IN_PROGRESS: 'admin.support.status.inProgress',
  RESOLVED: 'admin.support.status.resolved',
};

const STATUS_TONES: Readonly<Record<TicketStatus, StatusTone>> = {
  OPEN: 'warning',
  IN_PROGRESS: 'info',
  RESOLVED: 'success',
};

/** The two category keys the platform writes itself — see this file's note. */
const CATEGORY_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  daily_fee_refund: 'admin.support.category.dailyFeeRefund',
  driver_qr_dispute: 'admin.support.category.driverQrDispute',
};

export function categoryLabel(category: string, t: AdminTranslator): string {
  const resource = CATEGORY_LABELS[category];
  return resource ? t(resource) : category;
}

export function statusPill(status: TicketStatus, t: AdminTranslator): PillView {
  return { tone: STATUS_TONES[status] ?? 'neutral', label: t(STATUS_LABELS[status] ?? 'admin.support.status.open') };
}

/**
 * The Finance pill the wireframe puts on the refund-request row.
 *
 * `null` for an ordinary Support ticket: a pill on every row saying "this one is
 * yours" is a pill nobody reads. What matters is the exception — a ticket that
 * ends in money moving, which URD §2.3 gives to the Finance Officer and which a
 * CSR must not settle.
 */
export function queuePill(category: string, t: AdminTranslator): PillView | null {
  return ticketQueue(category) === 'finance'
    ? { tone: 'info', label: t('admin.support.queue.finance') }
    : null;
}

export interface TicketRowView {
  readonly key: string;
  readonly ticketId: string;
  /** The queue link — the agent's filters, plus this ticket in the reading pane. */
  readonly href: string;
  readonly selected: boolean;
  readonly category: string;
  readonly status: PillView;
  readonly financeQueue: PillView | null;
  readonly raised: string | null;
}

export function ticketRows(
  rows: readonly TicketRow[],
  selection: TicketSelection,
  { t, locale }: RenderContext,
): TicketRowView[] {
  return rows.map((row) => ({
    key: row.ticketId,
    ticketId: row.ticketId,
    href: ticketHref(selection, { ticketId: row.ticketId }),
    selected: selection.ticketId === row.ticketId,
    category: categoryLabel(row.category, t),
    status: statusPill(row.status, t),
    financeQueue: queuePill(row.category, t),
    raised: formatDateTime(locale, row.createdAt),
  }));
}

/**
 * One entry in what the screen draws as the ticket's thread.
 *
 * **Two entries where the platform holds more.** support-svc's own `TicketRow`
 * carries `thread` — every status transition and every reply, oldest first, which
 * is what makes a resolution readable rather than a status to interpret — and
 * admin-bff's mapping keeps only `description` and the latest `response`. So this
 * is the honest rendering of what reaches the portal, shaped so that the day the
 * gap closes it is a longer list rather than a different screen. C107 handoff.
 */
export interface ThreadEntry {
  readonly key: string;
  readonly author: string;
  readonly at: string | null;
  readonly body: string;
}

export interface TicketDetailView {
  readonly ticketId: string;
  readonly userId: string;
  readonly category: string;
  readonly status: PillView;
  readonly financeQueue: PillView | null;
  readonly raised: string | null;
  readonly resolvedAt: string | null;
  readonly resolved: boolean;
  readonly thread: readonly ThreadEntry[];
}

export function ticketDetail(row: TicketRow, { t, locale }: RenderContext): TicketDetailView {
  const raised = formatDateTime(locale, row.createdAt);
  const resolvedAt = formatDateTime(locale, row.resolvedAt);

  const thread: ThreadEntry[] = [];

  if (row.description?.trim()) {
    thread.push({
      key: 'raised',
      author: t('admin.support.thread.raiser'),
      at: raised,
      body: row.description.trim(),
    });
  }

  if (row.response?.trim()) {
    thread.push({
      key: 'response',
      author: t('admin.support.thread.agent'),
      // Null while a reply exists on a ticket nobody has closed: `resolvedAt` is
      // the only instant on the payload, and dating a reply with it would be a
      // guess. The words are the point; the clock is not.
      at: resolvedAt,
      body: row.response.trim(),
    });
  }

  return {
    ticketId: row.ticketId,
    userId: row.userId,
    category: categoryLabel(row.category, t),
    status: statusPill(row.status, t),
    financeQueue: queuePill(row.category, t),
    raised,
    resolvedAt,
    resolved: row.status === 'RESOLVED',
    thread,
  };
}

/** One read-only directory the person who raised a ticket can be looked up in. */
export interface TicketLookup {
  readonly key: string;
  readonly label: string;
  readonly href: string;
}

/**
 * The directory links, drawn from the caller's own menu.
 *
 * **Both directories, or as many of them as the operator holds.** `TicketRow`
 * carries a `userId` and nothing that says whether the account is a passenger's or
 * a driver's — a daily-fee refund is raised by a driver, a fare complaint by a
 * passenger, and both are `iam.users` ids. Offering the two and letting the wrong
 * one answer 404 is honest; guessing from the category would be a portal deciding
 * which directory somebody is in.
 *
 * The path comes from the item `GET /v1/admin/session` sent, never from
 * `src/server/routes.ts`: the server's own path is the one its own gate agrees
 * with, and an item the caller does not hold is not drawn at all — a link the
 * proxy would refuse is worse than no link (`AlertsCard`'s rule, AL-06).
 */
export function lookupLinks(
  menu: readonly AdminMenuGroup[],
  userId: string,
  t: AdminTranslator,
): TicketLookup[] {
  const directories = [
    { key: 'passengers', label: t('admin.support.lookup.passenger') },
    { key: 'drivers', label: t('admin.support.lookup.driver') },
  ];

  return directories.flatMap(({ key, label }) => {
    const path = screenPath(menu, key);
    return path ? [{ key, label, href: `${path}/${userId}` }] : [];
  });
}

/**
 * SCR-AP-006's refund queue, when the caller's menu carries it.
 *
 * URD §2.3's Refunds row gives the Support CSR `◐ raise/recommend`, which is Read
 * plus Raise and **not** Write — so the nav item is theirs, the queue opens, and
 * the button on it does not. That is the whole of the C107 fence: the hand-off is
 * a link to where the decision is taken, and this screen posts nothing.
 */
export function refundQueueHref(menu: readonly AdminMenuGroup[]): string | null {
  return screenPath(menu, 'refunds') ?? null;
}

function screenPath(menu: readonly AdminMenuGroup[], navKey: string): string | undefined {
  return permittedItems(menu).find(({ item }) => item.key === navKey)?.item.path;
}

/** How many tickets this page answered — `100+` past one page, `—` when it failed. */
export function queueTotal(
  page: CursorPage<TicketRow> | null,
  { t, locale }: RenderContext,
): string {
  if (!page) return '—';

  const count = formatCount(locale, page.items.length);
  return t(page.hasMore ? 'admin.support.queue.totalMore' : 'admin.support.queue.total', { count });
}
