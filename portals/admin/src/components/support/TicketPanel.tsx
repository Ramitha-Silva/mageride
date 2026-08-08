import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { TicketDetailView, TicketLookup } from './model';
import { ResolveTicketForm, type ResolveTicketLabels } from './ResolveTicketForm';

/**
 * SCR-AP-005's right-hand pane: one ticket, its thread, the read-only lookups and
 * the refund hand-off (US-14.13, US-16.3).
 *
 * ## The lookup is a link, and only when the operator's menu carries it
 *
 * D2 gives this screen a "read-only trip/user lookup" and the wireframe draws it
 * as a small table of trip facts. Neither is on this payload: admin-bff's
 * `TicketRow` carries a `userId` and drops support-svc's `tripId`, so the trip
 * cannot be named at all and the person can only be *pointed at* — the
 * directories (SCR-AP-010…015) are where a record is read, they are separately
 * gated, and they write the `PII_READ` row that opening one is. The link is drawn
 * from the item `GET /v1/admin/session` sent, never from the portal's own route
 * table: `AlertsCard`'s rule, because a link built from anything else promises a
 * screen `proxy.ts` then refuses.
 *
 * Both directories are offered because the row does not say which the account is
 * in — `userId` is an `iam.users` id whether it belongs to a passenger or to the
 * driver who raised a daily-fee refund.
 *
 * ## The refund hand-off moves nobody's money
 *
 * The fence, stated: **a refund is raised and executed on SCR-AP-006**, never
 * here. URD §2.3 gives the CSR `◐ raise/recommend` on Refunds, which opens that
 * queue and withholds its button — so this panel says what happens next and links
 * to the queue when the operator's menu has it. It posts nothing. A ticket whose
 * category is Finance's is *already* on Finance's pile (support-svc derives the
 * queue from the category and never stores it), which is why there is no
 * "escalate" button to press: the routing happened when the ticket was raised.
 */

export interface TicketPanelLabels {
  readonly raisedBy: string;
  readonly threadHeading: string;
  readonly threadEmpty: string;
  readonly lookupHeading: string;
  readonly lookupNote: string;
  readonly lookupNone: string;
  readonly refundHeading: string;
  readonly refundNote: string;
  readonly refundLink: string;
  readonly resolvedHeading: string;
  readonly resolvedNote: string;
  readonly resolve: ResolveTicketLabels;
}

export function TicketPanel({
  detail,
  status,
  category,
  lookups,
  refundHref,
  labels,
}: {
  detail: TicketDetailView;
  /** The queue filters, forwarded to the resolve action so it can return to them. */
  status: string;
  category: string;
  lookups: readonly TicketLookup[];
  /** SCR-AP-006's refund queue, when the operator's menu carries it. */
  refundHref: string | null;
  labels: TicketPanelLabels;
}) {
  return (
    <section className="flex min-w-0 flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{detail.category}</h2>
        <StatusPill tone={detail.status.tone}>{detail.status.label}</StatusPill>
        {detail.financeQueue ? (
          <StatusPill tone={detail.financeQueue.tone} dot={false}>
            {detail.financeQueue.label}
          </StatusPill>
        ) : null}
        <span className="flex-1" />
        <span className="break-all text-caption text-on-surface-variant">{detail.ticketId}</span>
      </div>

      <p className="text-caption text-on-surface-variant">
        {labels.raisedBy} <span className="break-all">{detail.userId}</span>
      </p>

      <div className="flex flex-col gap-xs">
        <h3 className="text-label font-semibold text-on-surface">{labels.threadHeading}</h3>
        {detail.thread.length === 0 ? (
          <p className="text-body-sm text-on-surface-variant">{labels.threadEmpty}</p>
        ) : (
          <ol className="flex flex-col gap-xs">
            {detail.thread.map((entry) => (
              <li key={entry.key} className="rounded-md bg-surface-variant p-xs">
                <p className="flex flex-wrap items-baseline gap-xs">
                  <span className="text-label font-semibold text-on-surface">{entry.author}</span>
                  {entry.at ? (
                    <span className="text-caption text-on-surface-variant">{entry.at}</span>
                  ) : null}
                </p>
                <p className="whitespace-pre-wrap text-body-sm text-on-surface">{entry.body}</p>
              </li>
            ))}
          </ol>
        )}
      </div>

      <div className="flex flex-col gap-xxs">
        <h3 className="text-label font-semibold text-on-surface">{labels.lookupHeading}</h3>
        {lookups.length === 0 ? (
          <p className="text-caption text-on-surface-variant">{labels.lookupNone}</p>
        ) : (
          <ul className="flex flex-wrap gap-sm">
            {lookups.map((lookup) => (
              <li key={lookup.key}>
                <Link href={lookup.href} className="text-body-sm underline underline-offset-2">
                  {lookup.label}
                </Link>
              </li>
            ))}
          </ul>
        )}
        <p className="text-caption text-on-surface-variant">{labels.lookupNote}</p>
      </div>

      <div className="flex flex-col gap-xxs rounded-md border border-outline p-xs">
        <h3 className="text-label font-semibold text-on-surface">{labels.refundHeading}</h3>
        <p className="text-body-sm text-on-surface-variant">{labels.refundNote}</p>
        {refundHref ? (
          <Link href={refundHref} className="text-body-sm underline underline-offset-2">
            {labels.refundLink}
          </Link>
        ) : null}
      </div>

      {detail.resolved ? (
        <div className="flex flex-col gap-xxs">
          <h3 className="text-label font-semibold text-on-surface">{labels.resolvedHeading}</h3>
          <p className="text-body-sm text-on-surface-variant">{labels.resolvedNote}</p>
        </div>
      ) : (
        <ResolveTicketForm
          ticketId={detail.ticketId}
          status={status}
          category={category}
          labels={labels.resolve}
        />
      )}
    </section>
  );
}
