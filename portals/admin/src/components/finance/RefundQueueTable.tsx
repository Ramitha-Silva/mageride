import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { RefundRowView } from './model';

/**
 * SCR-AP-006's refund queue — **two populations on one screen**, which is the
 * point of it (E-05, ADD §11.14).
 *
 * A `refund` row is a `fares.refunds` row somebody already raised and that is
 * awaiting settlement. An `overpaid` row is a payment §11.14 moved to `Overpaid`
 * that **nobody has raised a refund for** — the R-19 failure this queue exists to
 * catch. Showing only the first would hide exactly the case that needs an
 * operator, so the source is a column rather than a filter the screen applies.
 *
 * **Only an `overpaid` row offers the raise form.** A row that already has a
 * `refundId` has a refund; raising a second against the same payment is what
 * `409 payment-already-settled` is for, and drawing the button would be inviting
 * it.
 *
 * **A Support CSR sees this table and no button.** URD §2.3's Refunds row gives
 * them `◐ raise/recommend` against Finance's `✅ approve/execute` — so the queue
 * is theirs to read and the write is not. That is decided by admin-bff on
 * `Refunds · Write` and shows up here as the form not being rendered
 * (`RaiseRefundForm` is drawn only when the caller's menu carries the write).
 */

export interface RefundQueueLabels {
  readonly heading: string;
  readonly caption: string;
  readonly note: string;
  readonly columnSource: string;
  readonly columnPassenger: string;
  readonly columnPayment: string;
  readonly columnAmount: string;
  readonly columnStatus: string;
  readonly columnRequested: string;
  readonly columnAction: string;
  readonly raise: string;
  readonly ofPayment: string;
  readonly empty: string;
}

export function RefundQueueTable({
  rows,
  canRaise,
  labels,
}: {
  rows: readonly RefundRowView[];
  /** Whether the caller holds `Refunds · Write`. A CSR reads this queue and cannot execute. */
  canRaise: boolean;
  labels: RefundQueueLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-caption text-on-surface-variant">{labels.note}</p>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.columnSource}</TH>
            <TH>{labels.columnPassenger}</TH>
            <TH>{labels.columnPayment}</TH>
            <TH className="text-right">{labels.columnAmount}</TH>
            <TH>{labels.columnStatus}</TH>
            <TH>{labels.columnRequested}</TH>
            {canRaise ? <TH>{labels.columnAction}</TH> : null}
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={canRaise ? 7 : 6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>
                  <StatusPill tone={row.source.tone} dot={false}>
                    {row.source.label}
                  </StatusPill>
                </TD>
                <TD className="break-all">{row.passenger}</TD>
                <TD>
                  <span className="font-mono text-caption break-all">{row.paymentId}</span>
                  <span className="mt-xxs block text-caption text-on-surface-variant">
                    {row.method} · {row.paymentState}
                  </span>
                </TD>
                <TD className="text-right tabular-nums">
                  {row.amount}
                  {/* The ceiling a refund cannot exceed. On an `overpaid` row the
                      two are the same figure; on a partial they are not, and the
                      operator is about to type a number against one of them. */}
                  <span className="mt-xxs block text-caption text-on-surface-variant">
                    {labels.ofPayment} {row.paymentAmount}
                  </span>
                </TD>
                <TD>
                  {row.status ?? '—'}
                  {row.reasonCode ? (
                    <span className="mt-xxs block text-caption text-on-surface-variant">
                      {row.reasonCode}
                    </span>
                  ) : null}
                </TD>
                <TD className="whitespace-nowrap">{row.requestedAt ?? '—'}</TD>
                {canRaise ? (
                  <TD>
                    {row.raiseHref ? (
                      <Link href={row.raiseHref} className="text-body-sm underline underline-offset-2">
                        {labels.raise}
                      </Link>
                    ) : (
                      '—'
                    )}
                  </TD>
                ) : null}
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
