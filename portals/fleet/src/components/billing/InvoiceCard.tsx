import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty, type StatusTone } from '@mageride/ui';

import { PayInvoiceForm } from './PayInvoiceForm';
import type { InvoiceLineRow, InvoiceSummaryLine } from './billing-model';

/**
 * **SCR-FP-010's "Monthly invoice — June 2026" card** (US-13.10).
 *
 * ## The sketch's table, and the per-vehicle breakdown under it
 *
 * The wireframe draws Item / Qty / Rate / Amount with a Mode B row, a Mode A row
 * and a total. That is the summary; **US-13.10 is about the breakdown**, "a single
 * consolidated monthly invoice with a per-vehicle line breakdown (so the operator
 * pays once, but the amount is the sum of per-Mode-B-vehicle monthly fees)", which
 * is the second table. `GET …/billing/{invoiceId}` is the route C060 added for
 * exactly this, and before it "nothing could read the breakdown that is the whole
 * point of the story".
 *
 * ## Mode A is on the card and is not on the invoice
 *
 * There is no Mode A line and there cannot be one — a line exists only for a
 * charge `billing.monthly_subscriptions` raised, and that table carries Mode B
 * rows only (AL-03). The Mode A row's count is today's roster and its amount is
 * zero; the caption says which of the two it is, because an operator reconciling
 * an old invoice against a fleet that has since grown would otherwise find a
 * number that moved.
 *
 * ## Download is the platform's document, not a second one
 *
 * Both links go to `/billing/export`, which streams
 * `GET …/billing/{invoiceId}/export?format=…` — fleet-billing-svc's own CSV and
 * PDF. The CSV "prints money twice, rupees and integer minor units", and its TOTAL
 * is Σ of the rows above it computed from the lines. A file this portal composed
 * would be a second document about the same money.
 *
 * A server component; the Pay button is its one client child.
 */

export interface InvoiceCardLabels {
  readonly heading: string;
  readonly caption: string;
  readonly item: string;
  readonly qty: string;
  readonly rate: string;
  readonly amount: string;
  readonly modeANote: string;
  readonly reconcileWarning: string;
  readonly linesHeading: string;
  readonly linesCaption: string;
  readonly lineVehicle: string;
  readonly lineType: string;
  readonly lineAmount: string;
  readonly lineStatus: string;
  readonly linesEmpty: string;
  readonly downloadCsv: string;
  readonly downloadPdf: string;
  readonly receipt: string;
  readonly dates: readonly string[];
}

export interface InvoiceCardProps {
  readonly heading: string;
  readonly status: string;
  readonly statusTone: StatusTone;
  readonly summary: readonly InvoiceSummaryLine[];
  readonly lines: readonly InvoiceLineRow[];
  readonly reconciles: boolean;
  readonly csvHref: string;
  readonly pdfHref: string;
  /** Present only for a settled invoice — `getFleetInvoiceReceipt` is 404 otherwise. */
  readonly receipt: string | null;
  /** The invoice id, when this month can still be settled. `null` hides the button. */
  readonly payableInvoiceId: string | null;
  readonly payLabels: { readonly submit: string; readonly submitting: string };
  readonly labels: InvoiceCardLabels;
}

export function InvoiceCard({
  heading,
  status,
  statusTone,
  summary,
  lines,
  reconciles,
  csvHref,
  pdfHref,
  receipt,
  payableInvoiceId,
  payLabels,
  labels,
}: InvoiceCardProps) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card"
    >
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="flex-1 text-subtitle font-semibold">{heading}</h2>
        <StatusPill tone={statusTone}>{status}</StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.item}</TH>
            <TH>{labels.qty}</TH>
            <TH>{labels.rate}</TH>
            <TH>{labels.amount}</TH>
          </TR>
        </THead>
        <TBody>
          {summary.map((row) => (
            <TR key={row.key}>
              <TD className={row.total ? 'font-semibold' : ''}>{row.item}</TD>
              <TD className="tabular-nums">{row.qty}</TD>
              <TD className="tabular-nums">{row.rate}</TD>
              <TD className={`tabular-nums ${row.total ? 'font-semibold' : ''}`}>{row.amount}</TD>
            </TR>
          ))}
        </TBody>
      </Table>

      {/*
        `lineSumMinor` "is returned rather than assumed so a client can check
        rather than trust". If the three figures ever disagree, the screen says so
        rather than printing whichever one it happened to read.
      */}
      {reconciles ? null : (
        <p role="alert" className="text-body-sm text-error">
          {labels.reconcileWarning}
        </p>
      )}

      <p className="text-caption text-on-surface-variant">{labels.modeANote}</p>

      <div className="flex flex-col gap-xs">
        <h3 className="text-body font-semibold">{labels.linesHeading}</h3>
        <Table caption={labels.linesCaption}>
          <THead>
            <TR>
              <TH>{labels.lineVehicle}</TH>
              <TH>{labels.lineType}</TH>
              <TH>{labels.lineAmount}</TH>
              <TH>{labels.lineStatus}</TH>
            </TR>
          </THead>
          <TBody>
            {lines.length === 0 ? (
              <TableEmpty colSpan={4}>{labels.linesEmpty}</TableEmpty>
            ) : (
              lines.map((line) => (
                <TR key={line.key}>
                  <TD className="whitespace-nowrap">{line.vehicle}</TD>
                  <TD className="whitespace-nowrap">{line.vehicleType}</TD>
                  <TD className="whitespace-nowrap tabular-nums">{line.amount}</TD>
                  <TD>
                    <StatusPill tone={line.statusTone}>{line.status}</StatusPill>
                  </TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </div>

      {labels.dates.length > 0 ? (
        <dl className="flex flex-wrap gap-x-md gap-y-xxs text-caption text-on-surface-variant">
          {labels.dates.map((line) => (
            <dd key={line}>{line}</dd>
          ))}
        </dl>
      ) : null}

      {receipt ? <p className="text-body-sm text-on-surface-variant">{receipt}</p> : null}

      <div className="flex flex-wrap items-center gap-xs">
        {/*
          Plain anchors, not `next/link`: each is a download of a different content
          type rather than a navigation into the router's tree, and prefetching one
          on hover would render a PDF nobody asked for.
        */}
        <a
          href={csvHref}
          download
          className="inline-flex h-10 items-center justify-center rounded-sm border border-outline bg-surface px-md text-body-sm font-body text-on-surface hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          {labels.downloadCsv}
        </a>
        <a
          href={pdfHref}
          download
          className="inline-flex h-10 items-center justify-center rounded-sm border border-outline bg-surface px-md text-body-sm font-body text-on-surface hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          {labels.downloadPdf}
        </a>

        {payableInvoiceId ? (
          <PayInvoiceForm invoiceId={payableInvoiceId} labels={payLabels} />
        ) : null}
      </div>
    </section>
  );
}
