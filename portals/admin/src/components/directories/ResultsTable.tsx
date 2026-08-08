import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { CellView, TableRowView } from './model';

/**
 * The table every directory screen draws — the three result lists and the thirteen
 * activity tabs behind them.
 *
 * One component, because they are one thing: a header row, cells that are text or
 * a pill, and — on a result list only — a control that opens the record. Thirteen
 * near-identical tables would be thirteen places for a column to drift out of step
 * with its heading.
 *
 * **The row's control is a `<Link>`, and the whole row is not clickable.** A row
 * that navigates on click cannot be selected, copied out of, or middle-clicked into
 * a second tab — and an operator comparing two passengers does all three. It also
 * gives the action an accessible name of its own: forty rows announcing "Open" is a
 * list a screen reader cannot act on, so `openNamed` carries the subject.
 *
 * At 375px `Table` scrolls inside its own container rather than making the page
 * scroll sideways, which is what D2 §AP's three widths need from a directory of six
 * columns.
 */

export interface ResultsTableLabels {
  readonly caption: string;
  readonly columns: readonly string[];
  readonly empty: string;
  /** The visible word on a row's control. Absent where rows open nothing. */
  readonly open?: string;
  readonly action?: string;
}

function Cell({ cell }: { cell: CellView }) {
  return (
    <>
      {cell.pill ? <StatusPill tone={cell.pill.tone}>{cell.pill.label}</StatusPill> : null}
      {cell.pills?.map((pill) => (
        <StatusPill key={pill.label} tone={pill.tone}>
          {pill.label}
        </StatusPill>
      ))}
      {cell.text ? <span className="block">{cell.text}</span> : null}
      {cell.sub ? (
        <span className="block text-caption break-all text-on-surface-variant">{cell.sub}</span>
      ) : null}
    </>
  );
}

export function ResultsTable({
  rows,
  labels,
}: {
  readonly rows: readonly TableRowView[];
  readonly labels: ResultsTableLabels;
}) {
  const actionable = Boolean(labels.open);
  const columns = labels.columns.length + (actionable ? 1 : 0);

  return (
    <Table caption={labels.caption}>
      <THead>
        <TR>
          {labels.columns.map((column) => (
            <TH key={column}>{column}</TH>
          ))}
          {actionable ? <TH className="sr-only">{labels.action ?? labels.open}</TH> : null}
        </TR>
      </THead>
      <TBody>
        {rows.length === 0 ? (
          <TableEmpty colSpan={columns}>{labels.empty}</TableEmpty>
        ) : (
          rows.map((row) => (
            <TR key={row.key}>
              {row.cells.map((cell, index) => (
                <TD
                  key={labels.columns[index] ?? String(index)}
                  className={cell.numeric ? 'text-right tabular-nums' : undefined}
                >
                  <Cell cell={cell} />
                </TD>
              ))}
              {actionable ? (
                <TD className="text-right">
                  {row.href ? (
                    <Link
                      href={row.href}
                      aria-label={row.openNamed ?? labels.open}
                      className="inline-flex h-10 items-center rounded-sm border border-outline bg-secondary-container px-md text-body-sm whitespace-nowrap text-on-surface hover:bg-secondary-container/80"
                    >
                      {labels.open}
                      <span aria-hidden="true">{' ›'}</span>
                    </Link>
                  ) : null}
                </TD>
              ) : null}
            </TR>
          ))
        )}
      </TBody>
    </Table>
  );
}

/**
 * The next page, when the cursor says there is one.
 *
 * **Forward only, and that is the whole of what a cursor can honestly offer.** An
 * opaque continuation token names the page after this one and nothing else (D3'
 * §0), so a numbered pager would be this screen inventing a position in a list
 * whose length the platform never sent. Back is the browser's Back, which works
 * because every page of results is its own URL.
 */
export function MoreResults({ href, label }: { href: string; label: string }) {
  return (
    <div className="flex justify-center">
      <Link
        href={href}
        className="inline-flex h-10 items-center rounded-sm border border-outline px-md text-body-sm text-on-surface-variant hover:bg-surface-variant"
      >
        {label}
        <span aria-hidden="true">{' ›'}</span>
      </Link>
    </div>
  );
}
