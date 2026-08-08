import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  findTicket,
  ticketSearch,
  ticketSelection,
  SUPPORT_TICKETS_PATH,
  type CursorPage,
  type TicketRow,
} from '@/api/support';
import { ProblemPanel } from '@/components/ProblemPanel';
import {
  lookupLinks,
  queueTotal,
  refundQueueHref,
  ticketDetail,
  ticketRows,
  type RenderContext,
} from '@/components/support/model';
import { TicketFilter } from '@/components/support/TicketFilter';
import { TicketPanel } from '@/components/support/TicketPanel';
import { TicketQueueList } from '@/components/support/TicketQueueList';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-005 · `support_tickets`** — the ticket queue, the ticket being worked,
 * and the two things a CSR may not do from here (US-14.13, US-16.3, D-35).
 *
 * ## One screen, two panes, and the pane is in the URL
 *
 * The wireframe draws the queue and the ticket side by side, and D2 gives them one
 * screen id — so the ticket being read is `?ticket=`, not a path segment. It also
 * has to be: **admin-bff exposes no ticket-detail route**, so the ticket an agent
 * is reading is a row out of the page they are reading it from, and a URL that
 * promised more than that would be a promise the portal cannot keep (see
 * `src/api/support.ts`). Keeping the selection in the query means the filters, the
 * pane and the scroll position survive a reload and paste into a hand-over note.
 *
 * ## What this screen deliberately cannot do
 *
 * **Move money.** A refund is raised and executed on SCR-AP-006 — URD §2.3 gives
 * the CSR `◐ raise/recommend` on Refunds, which opens that queue and withholds its
 * button. The panel says so and links there when the operator's menu carries it;
 * nothing here posts to a finance route.
 *
 * **Reply without closing.** support-svc has the route; admin-bff has none onto
 * it. Recorded rather than faked.
 */

export const dynamic = 'force-dynamic';

export default async function SupportTicketsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = ticketSelection(params);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<TicketRow> | null = null;
  let problem: ProblemDetails | null = null;

  try {
    page = await read<CursorPage<TicketRow>>({
      path: SUPPORT_TICKETS_PATH,
      searchParams: ticketSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const rows = page?.items ?? [];
  const selected = findTicket(rows, selection.ticketId);
  const detail = selected ? ticketDetail(selected, context) : null;

  const menu = session?.menu ?? [];
  const lookups = detail ? lookupLinks(menu, detail.userId, t) : [];

  return (
    <div className="flex flex-col gap-md">
      {selection.resolved ? (
        <p
          role="status"
          className="rounded-card border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {t('admin.support.resolve.done')}{' '}
          {t('admin.audit.recorded', { action: 'TICKET_RESOLVED' })}
        </p>
      ) : null}

      <TicketFilter
        selection={selection}
        filtered={Boolean(selection.status || selection.category)}
        labels={{
          status: t('admin.support.filter.status'),
          statusAll: t('admin.support.filter.statusAll'),
          statusOpen: t('admin.support.status.open'),
          statusInProgress: t('admin.support.status.inProgress'),
          statusResolved: t('admin.support.status.resolved'),
          category: t('admin.support.filter.category'),
          categoryHint: t('admin.support.filter.categoryHint'),
          apply: t('admin.support.filter.apply'),
          clear: t('admin.support.filter.clear'),
          total: queueTotal(page, context),
        }}
      />

      {problem ? <ProblemPanel problem={problem} /> : null}

      {page ? (
        <div className="flex flex-col gap-md lg:flex-row">
          <TicketQueueList
            rows={ticketRows(rows, selection, context)}
            labels={{
              heading: t('admin.support.queue.heading'),
              empty: t('admin.support.queue.empty'),
            }}
          />

          {detail ? (
            <TicketPanel
              detail={detail}
              status={selection.status ?? ''}
              category={selection.category ?? ''}
              lookups={lookups}
              refundHref={refundQueueHref(menu)}
              labels={{
                raisedBy: t('admin.support.detail.raisedBy'),
                threadHeading: t('admin.support.thread.heading'),
                threadEmpty: t('admin.support.thread.empty'),
                lookupHeading: t('admin.support.lookup.heading'),
                lookupNote: t('admin.support.lookup.note'),
                lookupNone: t('admin.support.lookup.none'),
                refundHeading: t('admin.support.refund.heading'),
                refundNote: t('admin.support.refund.note'),
                refundLink: t('admin.support.refund.link'),
                resolvedHeading: t('admin.support.resolved.heading'),
                resolvedNote: t('admin.support.resolved.note'),
                resolve: {
                  response: t('admin.support.resolve.response'),
                  responseHint: t('admin.support.resolve.responseHint'),
                  resolve: t('admin.support.resolve.submit'),
                  working: t('admin.support.resolve.working'),
                  audit: t('admin.audit.notice'),
                },
              }}
            />
          ) : (
            <section className="flex min-w-0 flex-1 flex-col gap-xs rounded-card border border-outline bg-background p-sm shadow-card">
              <h2 className="text-subtitle font-semibold text-on-surface">
                {t('admin.support.detail.noneHeading')}
              </h2>
              <p className="text-body-sm text-on-surface-variant">
                {t(
                  selection.ticketId
                    ? 'admin.support.detail.notInView'
                    : 'admin.support.detail.noneBody',
                )}
              </p>
              {page.hasMore ? (
                <StatusPill tone="warning" dot={false}>
                  {t('admin.support.queue.capped', { count: page.items.length })}
                </StatusPill>
              ) : null}
            </section>
          )}
        </div>
      ) : null}
    </div>
  );
}
