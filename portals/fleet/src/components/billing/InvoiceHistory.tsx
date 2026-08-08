import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { InvoiceHistoryRow } from './billing-model';

/**
 * **SCR-FP-010's months** — one row per Colombo month, newest first, which is the
 * order `getFleetBilling` answers in.
 *
 * The wireframe draws one invoice card and no list; the list is what makes the
 * card's month a *choice* rather than always the current one, and the contract
 * puts the history behind the same screen ("Invoice history. Newest month first").
 * Selecting a month is a URL — `?invoice={id}` — so an operator can send a
 * colleague the month they are asking about, and an id from another organisation
 * resolves to nothing here and to `404` at the service.
 *
 * A `FREE` month is a real row and not an omission: it is "the evidence the run
 * considered them" for an organisation whose vehicles were all in their first
 * month, or which runs Mode A only (AL-03).
 *
 * A server component.
 */

export interface InvoiceHistoryLabels {
  readonly heading: string;
  readonly caption: string;
  readonly period: string;
  readonly vehicles: string;
  readonly amount: string;
  readonly status: string;
  readonly empty: string;
  readonly more: string | null;
  readonly freeNote: string;
}

export function InvoiceHistory({
  rows,
  labels,
}: {
  rows: readonly InvoiceHistoryRow[];
  labels: InvoiceHistoryLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.period}</TH>
            <TH>{labels.vehicles}</TH>
            <TH>{labels.amount}</TH>
            <TH>{labels.status}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={4}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key} selected={row.selected}>
                <TD className="whitespace-nowrap">
                  <Link
                    href={row.href}
                    className="text-primary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
                  >
                    {row.period}
                  </Link>
                </TD>
                <TD className="tabular-nums">{row.vehicles}</TD>
                <TD className="whitespace-nowrap tabular-nums">{row.amount}</TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.status}</StatusPill>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.freeNote}</p>
      {labels.more ? <p className="text-caption text-on-surface-variant">{labels.more}</p> : null}
    </section>
  );
}
