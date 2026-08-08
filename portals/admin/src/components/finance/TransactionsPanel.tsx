import Link from 'next/link';

import { Button, Field, Input, Select, StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { TRANSACTION_KINDS, type TransactionsSelection } from '@/api/finance';

import type { TransactionRowView } from './model';

/**
 * SCR-AP-006's "Wallet ledger" tab: the four wallet money-movements, their filter,
 * and the two exports.
 *
 * **Four kinds and not twelve.** `billing.journal_entries.kind` admits twelve
 * values; admin-bff's report admits `topup`, `daily_fee`, `voucher_purchase` and
 * `driver_transfer`, because a report that showed all of them would put a trip
 * payment, a penalty settlement and a weekly payout under column headings that
 * describe none of the three. The dropdown is that enum rather than a subset this
 * screen chose.
 *
 * **One row is one money event, not one account leg.** A driver-to-driver transfer
 * appears once, with a From and a To — the projection `billing.wallet_transactions`
 * would give two rows and a report that summed it would double the platform's
 * transfer volume.
 *
 * **The two export links carry the filter and nothing else.** admin-bff renders
 * the CSV and the PDF from the same `IFinanceService` call that answers this table,
 * so "the export matches the screen" is one computation rather than two that agree
 * today; `transactionsHref` is the single place the query is built, and both links
 * and the table itself come out of it.
 */

export interface TransactionsLabels {
  readonly heading: string;
  readonly caption: string;
  readonly from: string;
  readonly to: string;
  readonly kind: string;
  readonly kindAll: string;
  readonly kindTopup: string;
  readonly kindDailyFee: string;
  readonly kindVoucherPurchase: string;
  readonly kindDriverTransfer: string;
  readonly party: string;
  readonly partyHint: string;
  readonly apply: string;
  readonly clear: string;
  readonly timezone: string;
  readonly exportCsv: string;
  readonly exportPdf: string;
  readonly pdfNote: string;
  readonly total: string;
  readonly columnWhen: string;
  readonly columnKind: string;
  readonly columnFrom: string;
  readonly columnTo: string;
  readonly columnAmount: string;
  readonly empty: string;
  readonly capped: string;
}

const KIND_LABEL = {
  topup: 'kindTopup',
  daily_fee: 'kindDailyFee',
  voucher_purchase: 'kindVoucherPurchase',
  driver_transfer: 'kindDriverTransfer',
} as const satisfies Record<(typeof TRANSACTION_KINDS)[number], keyof TransactionsLabels>;

export function TransactionsPanel({
  selection,
  rows,
  windowLabel,
  csvHref,
  pdfHref,
  capped,
  labels,
}: {
  selection: TransactionsSelection;
  rows: readonly TransactionRowView[];
  /** The window admin-bff actually applied, already rendered. */
  windowLabel: string | null;
  csvHref: string;
  pdfHref: string;
  capped: boolean;
  labels: TransactionsLabels;
}) {
  const filtered = Boolean(selection.from ?? selection.kind ?? selection.partyId);

  return (
    <section className="flex flex-col gap-sm">
      <form
        method="get"
        action="/finance/transactions"
        className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
      >
        <Field label={labels.from} className="w-[170px]">
          <Input type="date" name="from" defaultValue={selection.from ?? ''} />
        </Field>

        <Field label={labels.to} hint={labels.timezone} className="w-[170px]">
          <Input type="date" name="to" defaultValue={selection.to ?? ''} />
        </Field>

        <Field label={labels.kind} className="w-[190px]">
          <Select name="kind" defaultValue={selection.kind ?? ''}>
            <option value="">{labels.kindAll}</option>
            {TRANSACTION_KINDS.map((kind) => (
              <option key={kind} value={kind}>
                {labels[KIND_LABEL[kind]]}
              </option>
            ))}
          </Select>
        </Field>

        <Field label={labels.party} hint={labels.partyHint} className="min-w-[240px] flex-1">
          <Input
            type="search"
            name="partyId"
            defaultValue={selection.partyId ?? ''}
            maxLength={40}
            autoCapitalize="none"
            spellCheck={false}
          />
        </Field>

        <Button type="submit" size="compact">
          {labels.apply}
        </Button>

        {filtered ? (
          <Link
            href="/finance/transactions"
            className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
          >
            {labels.clear}
          </Link>
        ) : null}
      </form>

      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone="info">{labels.total}</StatusPill>
        {windowLabel ? (
          <span className="text-caption text-on-surface-variant">{windowLabel}</span>
        ) : null}
        <span className="flex-1" />
        <Link
          href={csvHref}
          className="inline-flex h-10 items-center rounded-sm border border-outline px-md text-body-sm text-on-surface hover:bg-surface-variant"
        >
          {labels.exportCsv}
        </Link>
        <Link
          href={pdfHref}
          className="inline-flex h-10 items-center rounded-sm border border-outline px-md text-body-sm text-on-surface hover:bg-surface-variant"
        >
          {labels.exportPdf}
        </Link>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.columnWhen}</TH>
            <TH>{labels.columnKind}</TH>
            <TH>{labels.columnFrom}</TH>
            <TH>{labels.columnTo}</TH>
            <TH className="text-right">{labels.columnAmount}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD className="whitespace-nowrap">{row.at ?? '—'}</TD>
                <TD>
                  {row.kind}
                  {row.description ? (
                    <span className="mt-xxs block text-caption text-on-surface-variant">
                      {row.description}
                    </span>
                  ) : null}
                </TD>
                <TD className="break-all">{row.from}</TD>
                <TD className="break-all">{row.to}</TD>
                <TD className="text-right tabular-nums">{row.amount}</TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      {capped ? <p className="text-caption text-on-surface-variant">{labels.capped}</p> : null}
      {/* The PDF renderer is WinAnsi and cannot draw Sinhala or Tamil (D-26). An
          operator choosing between two downloads should know that before they
          pick the one their report has to be read in. */}
      <p className="text-caption text-on-surface-variant">{labels.pdfNote}</p>
    </section>
  );
}
