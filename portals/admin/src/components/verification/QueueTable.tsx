import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { QueueCellView, QueueRowView } from './model';

/**
 * One queue's rows (SCR-AP-003).
 *
 * The wireframe's five columns: the subject, two the tab decides, the status
 * pill, and the control that opens the detail. The middle pair is what differs
 * between tabs — a driver and a vehicle carry *when this arrived* and *what is
 * flagged*, an organisation carries *how big it is* and *what state its evidence
 * is in* — so the headers are props and the shape stays put. A table that changed
 * width between tabs would move the status column under the officer's eye every
 * time they switched.
 *
 * **The heading says what the queue is, and the pill beside it says why a row is
 * in it.** "Manual / doubtful flags only" is the wireframe's own caption and it is
 * AL-27 in three words: an auto-verified document produces no `pending` field row
 * and therefore cannot appear here. It is not a filter this screen applies — it is
 * what the queue *is* (C063), which is why it is stated rather than implemented.
 */

export interface QueueTableLabels {
  readonly heading: string;
  readonly flagsOnly: string;
  readonly subject: string;
  readonly middleOne: string;
  readonly middleTwo: string;
  readonly status: string;
  readonly action: string;
  readonly review: string;
  readonly empty: string;
  /** The table's accessible name. */
  readonly caption: string;
}

function Cell({ cell }: { cell: QueueCellView }) {
  if (cell.pills) {
    return (
      <div className="flex flex-wrap gap-xxs">
        {cell.pills.map((pill) => (
          <StatusPill key={pill.label} tone={pill.tone}>
            {pill.label}
          </StatusPill>
        ))}
      </div>
    );
  }

  return <>{cell.text}</>;
}

export function QueueTable({
  rows,
  labels,
}: {
  rows: readonly QueueRowView[];
  labels: QueueTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.flagsOnly}
        </StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.subject}</TH>
            <TH>{labels.middleOne}</TH>
            <TH>{labels.middleTwo}</TH>
            <TH>{labels.status}</TH>
            <TH className="sr-only">{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>
                  <span className="block font-semibold">{row.primary}</span>
                  <span className="block text-caption break-all text-on-surface-variant">
                    {row.secondary}
                  </span>
                </TD>
                <TD className="whitespace-nowrap">
                  <Cell cell={row.cells[0]} />
                </TD>
                <TD>
                  <Cell cell={row.cells[1]} />
                </TD>
                <TD>
                  <StatusPill tone={row.status.tone}>{row.status.label}</StatusPill>
                </TD>
                <TD className="text-right">
                  <Link
                    href={row.href}
                    className="inline-flex h-10 items-center rounded-sm border border-outline bg-secondary-container px-md text-body-sm text-on-surface hover:bg-secondary-container/80"
                  >
                    {labels.review}
                    <span aria-hidden="true">{' ›'}</span>
                  </Link>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
