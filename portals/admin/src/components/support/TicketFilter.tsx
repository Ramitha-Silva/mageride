import Link from 'next/link';

import { Button, Field, Input, Select, StatusPill } from '@mageride/ui';

import { TICKET_STATUSES, type TicketSelection } from '@/api/support';

/**
 * SCR-AP-005's queue filters — `?status=` and `?category=`, the two
 * `listSupportTicketQueue` declares.
 *
 * **It is a `method="get"` form and holds no state.** A CSR who narrowed the pile
 * to `driver_qr_dispute` can reload it, bookmark it, and paste it to the colleague
 * they are handing over to; the back button steps through their filters rather
 * than out of the screen. `QueueFilter` and `StatsFilter` are the same shape for
 * the same reasons.
 *
 * **`category` is a text box, not a select.** `support.tickets.category` carries
 * no CHECK — clients pick from whatever the FAQ surface publishes and two of the
 * values are written by services — so a dropdown here would be this portal
 * inventing a closed vocabulary the platform deliberately does not have.
 *
 * The pane selection does not travel with the filters: Apply is "show me this
 * pile", and carrying a ticket id into a query that may not answer with it would
 * leave the reading pane pointing at nothing.
 */

export interface TicketFilterLabels {
  readonly status: string;
  readonly statusAll: string;
  readonly statusOpen: string;
  readonly statusInProgress: string;
  readonly statusResolved: string;
  readonly category: string;
  readonly categoryHint: string;
  readonly apply: string;
  readonly clear: string;
  /** "{count} in this queue" — the wireframe's topbar pill. */
  readonly total: string;
}

const STATUS_LABEL = {
  OPEN: 'statusOpen',
  IN_PROGRESS: 'statusInProgress',
  RESOLVED: 'statusResolved',
} as const satisfies Record<(typeof TICKET_STATUSES)[number], keyof TicketFilterLabels>;

export function TicketFilter({
  selection,
  labels,
  filtered,
}: {
  selection: TicketSelection;
  labels: TicketFilterLabels;
  filtered: boolean;
}) {
  return (
    <form
      method="get"
      action="/support/tickets"
      className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
    >
      <Field label={labels.status} className="w-[180px]">
        <Select name="status" defaultValue={selection.status ?? ''}>
          <option value="">{labels.statusAll}</option>
          {TICKET_STATUSES.map((status) => (
            <option key={status} value={status}>
              {labels[STATUS_LABEL[status]]}
            </option>
          ))}
        </Select>
      </Field>

      <Field label={labels.category} hint={labels.categoryHint} className="min-w-[220px] flex-1">
        <Input
          type="search"
          name="category"
          defaultValue={selection.category ?? ''}
          maxLength={60}
          autoCapitalize="none"
          spellCheck={false}
        />
      </Field>

      <Button type="submit" size="compact">
        {labels.apply}
      </Button>

      {filtered ? (
        <Link
          href="/support/tickets"
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
