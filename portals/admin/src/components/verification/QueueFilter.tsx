import Link from 'next/link';

import { Button, Field, Input, Select, StatusPill } from '@mageride/ui';

import { VERIFICATION_STATUSES, type QueueSelection } from '@/api/verification';

/**
 * SCR-AP-003's search box and status filter (D2 §SCR-AP-003: "search + status
 * filter"), plus the wireframe's topbar count of everything waiting.
 *
 * **It is a `method="get"` form and holds no state.** The filter is the URL, so a
 * queue an officer narrowed to one plate survives a reload, a bookmark and a link
 * pasted into a ticket — and the back button steps through their search rather
 * than out of the screen. `StatsFilter` made the same call on SCR-AP-002 for the
 * same reasons.
 *
 * **The tab travels with it.** A hidden `queue` input keeps the officer on the
 * queue they were reading when they press Apply; without it, searching from the
 * fleet-org tab would answer on the driving-licence one.
 *
 * The wireframe draws this in the topbar. The shell owns that bar — it carries
 * the one `<h1>`, which is the nav entry's own label — so the controls sit at the
 * top of the content instead, in the order and with the contents the wireframe
 * gives them.
 */

export interface QueueFilterLabels {
  readonly search: string;
  readonly searchHint: string;
  readonly status: string;
  readonly statusAll: string;
  readonly statusPending: string;
  readonly statusApproved: string;
  readonly statusRejected: string;
  readonly apply: string;
  readonly clear: string;
  /** "{count} pending" — the wireframe's topbar pill. */
  readonly total: string;
}

const STATUS_LABEL = {
  PENDING: 'statusPending',
  APPROVED: 'statusApproved',
  REJECTED: 'statusRejected',
} as const satisfies Record<(typeof VERIFICATION_STATUSES)[number], keyof QueueFilterLabels>;

export function QueueFilter({
  selection,
  labels,
  filtered,
}: {
  selection: QueueSelection;
  labels: QueueFilterLabels;
  /** Whether anything is narrowing the three queues, so Clear is worth drawing. */
  filtered: boolean;
}) {
  return (
    <form
      method="get"
      action="/verification"
      className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
    >
      <input type="hidden" name="queue" value={selection.queue} />

      <Field label={labels.search} hint={labels.searchHint} className="min-w-[220px] flex-1">
        <Input
          type="search"
          name="search"
          defaultValue={selection.search ?? ''}
          maxLength={100}
          autoCapitalize="none"
          spellCheck={false}
        />
      </Field>

      <Field label={labels.status} className="w-[180px]">
        <Select name="status" defaultValue={selection.status ?? ''}>
          <option value="">{labels.statusAll}</option>
          {VERIFICATION_STATUSES.map((status) => (
            <option key={status} value={status}>
              {labels[STATUS_LABEL[status]]}
            </option>
          ))}
        </Select>
      </Field>

      <Button type="submit" size="compact">
        {labels.apply}
      </Button>

      {filtered ? (
        <Link
          href={`/verification?queue=${selection.queue}`}
          className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
        >
          {labels.clear}
        </Link>
      ) : null}

      <span className="flex-1" />

      <StatusPill tone="warning" className="mb-xs">
        {labels.total}
      </StatusPill>
    </form>
  );
}
