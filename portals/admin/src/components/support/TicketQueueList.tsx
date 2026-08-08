import Link from 'next/link';

import { StatusPill } from '@mageride/ui';

import type { TicketRowView } from './model';

/**
 * SCR-AP-005's left-hand **Queue** card: the pile, and which of it is open in the
 * reading pane.
 *
 * A list of links rather than a table — the wireframe draws a narrow column of
 * ticket · status pairs beside the ticket being worked, and a row here carries no
 * columns to align. `aria-current="page"` marks the one being read, because the
 * background tint that shows it to everybody else says nothing to a screen reader.
 *
 * The reference is the ticket's own id, in full. It is a GUID and it is long, and
 * a truncated identifier is an ambiguous one — the wireframe's `#TK-4521` is a
 * series nothing on this platform mints (recorded in the C075 handoff too).
 */

export interface TicketQueueLabels {
  readonly heading: string;
  readonly empty: string;
}

export function TicketQueueList({
  rows,
  labels,
}: {
  rows: readonly TicketRowView[];
  labels: TicketQueueLabels;
}) {
  return (
    <section className="flex w-full shrink-0 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card lg:w-[280px]">
      <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>

      {rows.length === 0 ? (
        <p className="text-body-sm text-on-surface-variant">{labels.empty}</p>
      ) : (
        <ul className="flex flex-col gap-xxs">
          {rows.map((row) => (
            <li key={row.key}>
              <Link
                href={row.href}
                {...(row.selected ? { 'aria-current': 'page' as const } : {})}
                className={`flex flex-col gap-xxs rounded-md border p-xs hover:bg-surface-variant ${
                  row.selected ? 'border-primary bg-surface-variant' : 'border-transparent'
                }`}
              >
                <span className="flex flex-wrap items-center gap-xxs">
                  <span className="text-body-sm font-semibold text-on-surface">{row.category}</span>
                  <StatusPill tone={row.status.tone}>{row.status.label}</StatusPill>
                  {row.financeQueue ? (
                    <StatusPill tone={row.financeQueue.tone} dot={false}>
                      {row.financeQueue.label}
                    </StatusPill>
                  ) : null}
                </span>
                <span className="block break-all text-caption text-on-surface-variant">
                  {row.ticketId}
                </span>
                {row.raised ? (
                  <span className="block text-caption text-on-surface-variant">{row.raised}</span>
                ) : null}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
