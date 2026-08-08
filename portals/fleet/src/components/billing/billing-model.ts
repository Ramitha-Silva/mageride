import type { StatusTone } from '@mageride/ui';

import {
  invoiceStatusView,
  invoiceSummary,
  type FleetInvoice,
  type FleetInvoiceDetail,
  type FleetWallet,
  type FleetWalletMovement,
} from '@/api/billing';
import { formatFareMinor, formatInstant, formatMonth } from '@/i18n/format';
import type { FleetMessageKey, FleetTranslator, Locale } from '@/i18n';

import { vehicleTypeLabel } from '../vehicles/vehicle-model';

/**
 * SCR-FP-010's view model — one invoice, its per-vehicle breakdown, the months
 * behind it and the wallet's own statement, rendered on the server.
 *
 * Every figure on this screen is money, so every figure goes through
 * `formatFareMinor`: integer minor units are what crossed the wire (root
 * CLAUDE.md) and rupees are what an operator reconciles. The mark is in the
 * resource string, because where it sits relative to the number is a property of
 * the language.
 */

/* ---------------------------------------------------------------------------
 * The wireframe's Item / Qty / Rate / Amount table
 * ------------------------------------------------------------------------ */

export interface InvoiceSummaryLine {
  readonly key: string;
  readonly item: string;
  readonly qty: string;
  readonly rate: string;
  readonly amount: string;
  /** The bold "Total due" row. */
  readonly total: boolean;
}

const SUMMARY_LABELS: Readonly<Record<string, FleetMessageKey>> = {
  'mode-b': 'fleet.billing.summary.modeB',
  'mode-b-free': 'fleet.billing.summary.modeBFree',
  'mode-a': 'fleet.billing.summary.modeA',
};

/**
 * The sketch's four rows, from the invoice's own lines — plus the total, which is
 * Σ of them.
 *
 * **`modeAVehicles` is `null` when the roster could not be read**, and the row then
 * shows a dash rather than a zero. "0 Mode A vehicles" over a roster nobody could
 * list is a reassuring number with nothing behind it — the same call C114's
 * dashboard makes about its documents row.
 */
export function invoiceSummaryLines(
  detail: FleetInvoiceDetail,
  modeAVehicles: number | null,
  locale: Locale,
  t: FleetTranslator,
): InvoiceSummaryLine[] {
  const summary = invoiceSummary(detail, modeAVehicles ?? 0);
  const money = (minor: number) =>
    t('fleet.money.rupees', { amount: formatFareMinor(locale, minor) });

  const rows: InvoiceSummaryLine[] = summary.rows.map((row) => ({
    key: row.key,
    item: t(SUMMARY_LABELS[row.key] ?? 'fleet.billing.summary.modeB'),
    qty:
      row.key === 'mode-a' && modeAVehicles === null
        ? t('fleet.billing.unknownCount')
        : String(row.qty),
    rate: row.mixedRate
      ? t('fleet.billing.summary.mixedRate')
      : row.rateMinor === null
        ? t('fleet.billing.summary.free')
        : money(row.rateMinor),
    amount: money(row.amountMinor),
    total: false,
  }));

  rows.push({
    key: 'total',
    item: t('fleet.billing.summary.total'),
    qty: '',
    rate: '',
    amount: money(summary.totalMinor),
    total: true,
  });

  return rows;
}

/** Whether Σ lines, `lineSumMinor` and the invoice's own amount are one number. */
export function invoiceReconciles(detail: FleetInvoiceDetail): boolean {
  return invoiceSummary(detail, 0).reconciles;
}

/* ---------------------------------------------------------------------------
 * The per-vehicle breakdown (Δ C060 — the thing US-13.10 is about)
 * ------------------------------------------------------------------------ */

export interface InvoiceLineRow {
  readonly key: string;
  readonly vehicle: string;
  readonly vehicleType: string;
  readonly amount: string;
  readonly status: string;
  readonly statusTone: StatusTone;
}

/**
 * One row per vehicle that was billed, **from the snapshot and never the roster**.
 *
 * The plate and the type on a line are "the ones that were billed, not the
 * vehicle's current ones", so a vehicle re-plated last week still appears here
 * under the plate the invoice was raised against. Joining to the roster to
 * "improve" the plate would make a settled invoice change under an operator.
 */
export function invoiceLineRows(
  detail: FleetInvoiceDetail,
  locale: Locale,
  t: FleetTranslator,
): InvoiceLineRow[] {
  return detail.lines.map((line) => ({
    key: line.vehicleId,
    vehicle: line.registrationNumber,
    vehicleType: vehicleTypeLabel(line.vehicleType, t),
    amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, line.amountMinor) }),
    status:
      line.status === 'FREE'
        ? t('fleet.billing.line.firstMonthFree')
        : t('fleet.billing.line.charged'),
    statusTone: line.status === 'FREE' ? 'neutral' : 'info',
  }));
}

/* ---------------------------------------------------------------------------
 * The months
 * ------------------------------------------------------------------------ */

export interface InvoiceHistoryRow {
  readonly key: string;
  readonly period: string;
  readonly vehicles: string;
  readonly amount: string;
  readonly status: string;
  readonly statusTone: StatusTone;
  /** `?invoice={id}` — selecting a month is a URL, so it can be sent to somebody. */
  readonly href: string;
  readonly selected: boolean;
}

export function invoiceHistoryRows(
  invoices: readonly FleetInvoice[],
  selectedInvoiceId: string | null,
  locale: Locale,
  t: FleetTranslator,
): InvoiceHistoryRow[] {
  return invoices.map((invoice) => {
    const status = invoiceStatusView(invoice.status);

    return {
      key: invoice.invoiceId,
      period: formatMonth(locale, invoice.periodMonth) ?? invoice.periodMonth,
      vehicles:
        invoice.vehicleCount === undefined
          ? t('fleet.billing.unknownCount')
          : String(invoice.vehicleCount),
      amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, invoice.amountMinor) }),
      status: t(status.labelKey),
      statusTone: status.tone,
      href: `/billing?invoice=${invoice.invoiceId}`,
      selected: invoice.invoiceId === selectedInvoiceId,
    };
  });
}

/* ---------------------------------------------------------------------------
 * The wallet's statement
 * ------------------------------------------------------------------------ */

export interface WalletMovementRow {
  readonly key: string;
  readonly kind: string;
  readonly when: string;
  readonly amount: string;
  readonly balanceAfter: string;
  /** A settlement is posted negative; the row is drawn as the debit it is. */
  readonly debit: boolean;
}

const MOVEMENT_LABELS: Readonly<Record<string, FleetMessageKey>> = {
  topup: 'fleet.billing.movement.topup',
  fleet_invoice: 'fleet.billing.movement.invoice',
  adjustment: 'fleet.billing.movement.adjustment',
};

/**
 * The statement, newest first, as wallet-svc's ledger posted it.
 *
 * `amountMinor` is **signed as posted** — a settlement is negative — so the sign
 * comes off the ledger rather than being decided from `kind`. An `adjustment`
 * (US-14.11's Finance correction) can be either, and inferring its direction from
 * its name is how a credit would be drawn as a debit.
 */
export function walletMovementRows(
  wallet: FleetWallet,
  locale: Locale,
  t: FleetTranslator,
): WalletMovementRow[] {
  return wallet.movements.map((movement: FleetWalletMovement) => ({
    key: movement.entryId,
    kind: t(MOVEMENT_LABELS[movement.kind] ?? 'fleet.billing.movement.other'),
    when: formatInstant(locale, movement.ts) ?? movement.ts,
    amount: t('fleet.money.rupees', {
      amount: formatFareMinor(locale, Math.abs(movement.amountMinor)),
    }),
    balanceAfter: t('fleet.money.rupees', {
      amount: formatFareMinor(locale, movement.balanceAfterMinor),
    }),
    debit: movement.amountMinor < 0,
  }));
}

/* ---------------------------------------------------------------------------
 * Which month the screen is showing
 * ------------------------------------------------------------------------ */

/**
 * The invoice SCR-FP-010 opens on.
 *
 * `?invoice={id}` when the operator picked one — an id from another organisation
 * resolves to nothing here and to `404` at the service, so a pasted URL cannot
 * name somebody else's month. Otherwise the **newest**, which is the month an
 * operator opens a billing screen about; `GET …/billing` answers newest first, so
 * that is the first row.
 */
export function selectedInvoice(
  invoices: readonly FleetInvoice[],
  requested: string | undefined,
): FleetInvoice | null {
  if (requested) {
    return invoices.find((invoice) => invoice.invoiceId === requested) ?? null;
  }
  return invoices[0] ?? null;
}
