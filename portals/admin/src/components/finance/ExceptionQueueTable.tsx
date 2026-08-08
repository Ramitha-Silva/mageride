import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { ExceptionRowView } from './model';

/**
 * D6' §7.2's "exceptions → Finance queue", as the table under the settlement card.
 *
 * **The four kinds are derived from the row, not stored on it** — wallet-svc
 * records no exception column — so a session that resolves itself leaves this
 * queue with nobody having to close it. That is why there is no "dismiss" or
 * "mark handled" control here and there must not be one: it would be a second
 * source of truth for membership, and the first thing it could do wrong is hide a
 * mismatch that is still a mismatch.
 *
 * **Oldest first, exactly as admin-bff sent it.** Its own note says why: the queue
 * is worked oldest first and an operator touching a row must not move it to the
 * back. Nothing here re-sorts.
 */

export interface ExceptionLabels {
  readonly heading: string;
  readonly caption: string;
  readonly note: string;
  readonly kind: string;
  readonly gateway: string;
  readonly driver: string;
  readonly amount: string;
  readonly posted: string;
  readonly opened: string;
  readonly reference: string;
  readonly empty: string;
}

export function ExceptionQueueTable({
  rows,
  labels,
}: {
  rows: readonly ExceptionRowView[];
  labels: ExceptionLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-caption text-on-surface-variant">{labels.note}</p>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.kind}</TH>
            <TH>{labels.gateway}</TH>
            <TH>{labels.driver}</TH>
            <TH className="text-right">{labels.amount}</TH>
            <TH className="text-right">{labels.posted}</TH>
            <TH>{labels.reference}</TH>
            <TH>{labels.opened}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={7}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>
                  <StatusPill tone={row.kind.tone} dot={false}>
                    {row.kind.label}
                  </StatusPill>
                  {row.failureReason ? (
                    <span className="mt-xxs block text-caption text-on-surface-variant">
                      {row.failureReason}
                    </span>
                  ) : null}
                </TD>
                <TD>
                  {row.method}
                  {/* The `billing.topups.state` the session is actually in — an
                      `unsettled` row that is `Pending` and one that is `Failed`
                      are two different jobs. */}
                  <span className="mt-xxs block text-caption text-on-surface-variant">
                    {row.state}
                  </span>
                </TD>
                <TD>{row.driver}</TD>
                <TD className="text-right tabular-nums">{row.amount}</TD>
                {/* `—`, not `0`: an absent posting on a settled session IS the exception. */}
                <TD className="text-right tabular-nums">{row.posted}</TD>
                <TD className="font-mono text-caption break-all">{row.reference ?? '—'}</TD>
                <TD className="whitespace-nowrap">{row.opened ?? '—'}</TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
