import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import {
  hasReceipt,
  invoiceReceiptTarget,
  invoiceStatusView,
  invoiceTarget,
  isPayable,
  BILLING_PAGE_LIMIT,
  FLEET_BILLING,
  FLEET_WALLET,
  MAX_TOPUP_MINOR,
  MIN_TOPUP_MINOR,
  TOPUP_PENDING_WINDOW_SECONDS,
  WALLET_MOVEMENT_LIMIT,
  type FleetInvoiceDetail,
  type FleetInvoicePage,
  type FleetInvoiceReceipt,
  type FleetWallet,
} from '@/api/billing';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { VEHICLES, type FleetVehicleList } from '@/api/vehicles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { InvoiceCard } from '@/components/billing/InvoiceCard';
import { InvoiceHistory } from '@/components/billing/InvoiceHistory';
import { WalletPanel } from '@/components/billing/WalletPanel';
import {
  invoiceHistoryRows,
  invoiceLineRows,
  invoiceReconciles,
  invoiceSummaryLines,
  selectedInvoice,
  walletMovementRows,
} from '@/components/billing/billing-model';
import { formatDay, formatFareMinor, formatInstant, formatMonth } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { billingRefusal, canReadBilling } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-010 · `fleet_billing`** — the consolidated monthly invoice and the
 * fleet wallet it is settled from (US-13.10, US-13.10b).
 *
 * ## Nothing is read until the caller is known to be entitled
 *
 * `FleetBillingAccessFilter` gates every route in `fleet-billing.yaml` — **reads
 * included** — on the Owner sub-role and an APPROVED organisation, which is
 * stricter than fleet-svc and stricter than URD §2.3 alone: "US-13.A5 gives
 * billing to the Owner and takes it from the Manager in the same sentence", and "a
 * PENDING org has no approved vehicles, so it has no charges and no invoice".
 *
 * Both refusals are knowable before the request, so `canReadBilling()` decides
 * whether this screen reads anything at all. A Manager who follows a link here
 * gets a sentence naming whose screen it is, not four 403s — and no audit trail of
 * an access attempt nobody made. The nav does not offer them the entry either
 * (`src/server/routes.ts`), and `proxy.ts` refuses the route; this is the third
 * of the three, and it is the one that stops the reads.
 *
 * ## Four reads, and the month is a URL
 *
 * | part | route |
 * |---|---|
 * | the months | `GET …/billing?limit=12` |
 * | the invoice and its per-vehicle lines | `GET …/billing/{invoiceId}` |
 * | the balance and the statement | `GET …/wallet?limit=10` |
 * | how many Mode A vehicles are free | `GET …/vehicles` — **fleet-svc's, not billing's** |
 *
 * `?invoice={id}` picks the month; absent, the newest, which is what an operator
 * opens a billing screen about. The receipt is read only for a settled invoice,
 * because `getFleetInvoiceReceipt` answers `404` for anything else on purpose —
 * "a receipt is evidence that money moved" and an empty one would let this screen
 * render one for a bill nobody paid.
 *
 * The roster read is the only one that is not fleet-billing-svc's, and it exists
 * for one cell: the wireframe's "Mode A vehicles · 88 · Free · Rs 0". **No invoice
 * has a Mode A line** (AL-03), so that count cannot come from the invoice, and the
 * card says so rather than letting a roster figure read as a billed one.
 */

export const dynamic = 'force-dynamic';

export default async function BillingPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, query] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  if (!canReadBilling(session)) {
    return (
      <div className="flex flex-col gap-md">
        <h2 className="text-title font-display">{t('fleet.billing.title')}</h2>
        <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
          {billingRefusal(session) === 'pending-org'
            ? t('fleet.billing.pendingOrg')
            : t('fleet.billing.ownerOnly')}
        </p>
      </div>
    );
  }

  let invoices: FleetInvoicePage | null = null;
  let problem: ProblemDetails | null = null;
  try {
    invoices = await read<FleetInvoicePage>({
      org: FLEET_BILLING,
      searchParams: { limit: BILLING_PAGE_LIMIT },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const chosen = selectedInvoice(invoices?.items ?? [], single(query['invoice']));

  const [wallet, detail, receipt, modeAVehicles] = await Promise.all([
    optional<FleetWallet>({ org: FLEET_WALLET, searchParams: { limit: WALLET_MOVEMENT_LIMIT } }),
    chosen ? optional<FleetInvoiceDetail>({ org: invoiceTarget(chosen.invoiceId) }) : null,
    chosen && hasReceipt(chosen)
      ? optional<FleetInvoiceReceipt>({ org: invoiceReceiptTarget(chosen.invoiceId) })
      : null,
    modeACount(),
  ]);

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.billing.title')}</h2>
        {/*
          The sketch's topbar control. The top-up itself is one form and it lives
          on the wallet card, so this is the anchor to it rather than a second
          payment surface beside the first.
        */}
        <a
          href="#top-up"
          className="inline-flex h-10 items-center justify-center rounded-sm bg-primary px-md text-body-sm font-body text-on-primary hover:bg-primary/90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          {t('fleet.billing.topUp')}
        </a>
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        {detail ? (
          <InvoiceCard
            heading={t('fleet.billing.invoice.heading', {
              month:
                formatMonth(locale, detail.invoice.periodMonth) ?? detail.invoice.periodMonth,
            })}
            status={t(invoiceStatusView(detail.invoice.status).labelKey)}
            statusTone={invoiceStatusView(detail.invoice.status).tone}
            summary={invoiceSummaryLines(detail, modeAVehicles, locale, t)}
            lines={invoiceLineRows(detail, locale, t)}
            reconciles={invoiceReconciles(detail)}
            csvHref={`/billing/export?invoice=${detail.invoice.invoiceId}&format=csv`}
            pdfHref={`/billing/export?invoice=${detail.invoice.invoiceId}&format=pdf`}
            receipt={
              receipt
                ? t('fleet.billing.receipt.settled', {
                    date: formatInstant(locale, receipt.settledAt) ?? receipt.settledAt,
                    entry: receipt.journalEntryId,
                  })
                : null
            }
            payableInvoiceId={isPayable(detail.invoice) ? detail.invoice.invoiceId : null}
            payLabels={{
              submit: t('fleet.billing.pay.submit'),
              submitting: t('fleet.billing.pay.submitting'),
            }}
            labels={{
              heading: t('fleet.billing.invoice.label'),
              caption: t('fleet.billing.invoice.caption'),
              item: t('fleet.billing.column.item'),
              qty: t('fleet.billing.column.qty'),
              rate: t('fleet.billing.column.rate'),
              amount: t('fleet.billing.column.amount'),
              modeANote: t('fleet.billing.modeANote'),
              reconcileWarning: t('fleet.billing.reconcileWarning'),
              linesHeading: t('fleet.billing.lines.heading'),
              linesCaption: t('fleet.billing.lines.caption'),
              lineVehicle: t('fleet.billing.column.vehicle'),
              lineType: t('fleet.billing.column.vehicleType'),
              lineAmount: t('fleet.billing.column.amount'),
              lineStatus: t('fleet.billing.column.lineStatus'),
              linesEmpty: t('fleet.billing.lines.empty'),
              downloadCsv: t('fleet.billing.download.csv'),
              downloadPdf: t('fleet.billing.download.pdf'),
              receipt: t('fleet.billing.receipt.label'),
              dates: invoiceDates(detail, locale, t),
            }}
          />
        ) : (
          <p className="flex-1 rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
            {invoices && invoices.items.length === 0
              ? t('fleet.billing.noInvoices')
              : t('fleet.billing.invoiceUnavailable')}
          </p>
        )}

        {wallet ? (
          <WalletPanel
            balance={money(locale, t, wallet.balanceMinor)}
            outstanding={money(locale, t, wallet.outstandingMinor)}
            available={money(locale, t, wallet.availableMinor)}
            short={wallet.availableMinor < 0}
            movements={walletMovementRows(wallet, locale, t)}
            labels={{
              heading: t('fleet.billing.wallet.heading'),
              balance: t('fleet.billing.wallet.balance'),
              outstanding: t('fleet.billing.wallet.outstanding'),
              available: t('fleet.billing.wallet.available'),
              negativeNote: t('fleet.billing.wallet.shortfall'),
              statementHeading: t('fleet.billing.statement.heading'),
              statementCaption: t('fleet.billing.statement.caption'),
              movementKind: t('fleet.billing.column.movement'),
              movementWhen: t('fleet.billing.column.when'),
              movementAmount: t('fleet.billing.column.amount'),
              movementBalance: t('fleet.billing.column.balanceAfter'),
              statementEmpty: t('fleet.billing.statement.empty'),
              updatedAt: wallet.updatedAt
                ? t('fleet.billing.wallet.updatedAt', {
                    time: formatInstant(locale, wallet.updatedAt) ?? wallet.updatedAt,
                  })
                : null,
              topUp: {
                heading: t('fleet.billing.topup.heading'),
                amount: t('fleet.billing.topup.amount'),
                amountHint: t('fleet.billing.topup.amountHint', {
                  min: money(locale, t, MIN_TOPUP_MINOR),
                  max: money(locale, t, MAX_TOPUP_MINOR),
                }),
                method: t('fleet.billing.topup.method'),
                onepay: t('fleet.billing.topup.method.onepay'),
                onepayHint: t('fleet.billing.topup.onepayHint'),
                lankaqr: t('fleet.billing.topup.method.lankaqr'),
                lankaqrHint: t('fleet.billing.topup.lankaqrHint'),
                noBankTransfer: t('fleet.billing.topup.noBankTransfer'),
                required: t('fleet.org.required'),
                submit: t('fleet.billing.topup.submit'),
                submitting: t('fleet.billing.topup.submitting'),
                check: t('fleet.billing.topup.check'),
                checking: t('fleet.billing.topup.checking'),
                qrHeading: t('fleet.billing.topup.qrHeading'),
                qrHint: t('fleet.billing.topup.qrHint', {
                  seconds: TOPUP_PENDING_WINDOW_SECONDS,
                }),
              },
            }}
          />
        ) : (
          <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant lg:w-96">
            {t('fleet.billing.wallet.unavailable')}
          </p>
        )}
      </div>

      <InvoiceHistory
        rows={invoiceHistoryRows(
          invoices?.items ?? [],
          chosen?.invoiceId ?? null,
          locale,
          t,
        )}
        labels={{
          heading: t('fleet.billing.history.heading'),
          caption: t('fleet.billing.history.caption'),
          period: t('fleet.billing.column.period'),
          vehicles: t('fleet.billing.column.vehicles'),
          amount: t('fleet.billing.column.amount'),
          status: t('fleet.billing.column.status'),
          empty: t('fleet.billing.history.empty'),
          more: invoices?.hasMore
            ? t('fleet.billing.history.more', { months: BILLING_PAGE_LIMIT })
            : null,
          freeNote: t('fleet.billing.freeNote'),
        }}
      />
    </div>
  );
}

/** The four instants an invoice records, as the ones it actually has. */
function invoiceDates(
  detail: FleetInvoiceDetail,
  locale: Locale,
  t: FleetTranslator,
): string[] {
  const lines: string[] = [];
  const { invoice } = detail;

  if (invoice.dueAt) {
    lines.push(t('fleet.billing.date.due', { date: formatDay(locale, invoice.dueAt) ?? invoice.dueAt }));
  }
  // Written once and never moved, so it stays on a settled invoice as the record
  // that this month was chased.
  if (invoice.overdueAt) {
    lines.push(
      t('fleet.billing.date.overdue', {
        date: formatDay(locale, invoice.overdueAt) ?? invoice.overdueAt,
      }),
    );
  }
  if (invoice.settledAt) {
    lines.push(
      t('fleet.billing.date.settled', {
        date: formatDay(locale, invoice.settledAt) ?? invoice.settledAt,
      }),
    );
  }

  return lines;
}

function money(locale: Locale, t: FleetTranslator, minor: number): string {
  return t('fleet.money.rupees', { amount: formatFareMinor(locale, minor) });
}

/**
 * How many Mode A vehicles this organisation runs — the sketch's free row.
 *
 * `null` when the roster could not be read, so the cell shows a dash rather than a
 * zero. The roster is approval-gated and this screen is too, so in practice the
 * two succeed or fail together; the fallback is for the read failing on its own.
 */
async function modeACount(): Promise<number | null> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return (answer.items ?? []).filter((vehicle) => vehicle.mode === 'A').length;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}

/** A read whose failure costs a card rather than the screen. */
async function optional<T>(options: {
  readonly org: string;
  readonly searchParams?: Readonly<Record<string, string | number>>;
}): Promise<T | null> {
  try {
    return await read<T>(options);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}

/** The first value of a repeated query parameter, or `undefined`. */
function single(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
