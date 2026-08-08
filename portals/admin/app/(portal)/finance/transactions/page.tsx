import { read } from '@/api/client';
import {
  TRANSACTIONS_PATH,
  transactionsHref,
  transactionsSearch,
  transactionsSelection,
  type TransactionsReport,
} from '@/api/finance';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { transactionRows, type RenderContext } from '@/components/finance/model';
import { financeTabs } from '@/components/finance/tabs';
import { TransactionsPanel } from '@/components/finance/TransactionsPanel';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { formatDateRange, formatMoneyMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-006 · the Wallet-ledger tab** (US-9A.15) and, at
 * `?kind=driver_transfer`, the wireframe's **Credit transfers** tab.
 *
 * ## Credit transfers is a filter, not a screen
 *
 * D2 gives it its own tab and describes it as "read-only review of driver→driver
 * transfers (exact value, **no per-driver/per-transfer commission**, AL-01)". That
 * is `kind=driver_transfer` over the same report: same gate, same rows, same
 * export. Building it as a second screen would mean a second query that could
 * disagree with this one about what a transfer is — and the fact the tab exists to
 * show, that the amount that left equals the amount that arrived, is a property of
 * the row rather than of a view over it.
 *
 * The AL-01 note is rendered on that tab and only on that tab: a commission
 * disclaimer over the top-up rows would be answering a question nobody asked, and
 * the bulk-voucher discount — which *is* the reseller's margin — is charged at
 * purchase and configured in SCR-AP-007.
 *
 * ## The window on screen is the window admin-bff applied
 *
 * `TransactionsReport` echoes `from` and `to`, so the heading states the range that
 * was actually used rather than the range the form asked for. With no dates chosen
 * those differ — the contract defaults to the 29 days before today (D-38) — and a
 * report whose window is implied is one nobody can check later.
 */

export const dynamic = 'force-dynamic';

export default async function TransactionsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = transactionsSelection(params);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  const tabs = financeTabs(
    session?.menu ?? [],
    selection.kind === 'driver_transfer' ? 'transfers' : 'ledger',
  );

  let report: TransactionsReport | null = null;
  let problem: ProblemDetails | null = null;

  try {
    report = await read<TransactionsReport>({
      path: TRANSACTIONS_PATH,
      searchParams: transactionsSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  return (
    <div className="flex flex-col gap-md">
      <ScreenTabs
        navLabel={t('admin.finance.tabs.label')}
        tabs={tabs.map((tab) => ({
          id: tab.id,
          href: tab.href,
          label: t(tab.labelKey),
          current: tab.current,
        }))}
      />

      {selection.kind === 'driver_transfer' ? (
        <p className="rounded-card border border-outline bg-background p-sm text-body-sm text-on-surface-variant shadow-card">
          {t('admin.finance.transfers.note')}
        </p>
      ) : null}

      {problem ? <ProblemPanel problem={problem} /> : null}

      {report ? (
        <TransactionsPanel
          selection={selection}
          rows={transactionRows(report.items, context)}
          windowLabel={formatDateRange(locale, report.from, report.to)}
          csvHref={transactionsHref('/finance/transactions/export/csv', selection)}
          pdfHref={transactionsHref('/finance/transactions/export/pdf', selection)}
          capped={report.items.length >= 100}
          labels={{
            heading: t('admin.finance.ledger.heading'),
            caption: t('admin.finance.ledger.caption'),
            from: t('admin.finance.filter.from'),
            to: t('admin.finance.filter.to'),
            kind: t('admin.finance.filter.kind'),
            kindAll: t('admin.finance.filter.kindAll'),
            kindTopup: t('admin.finance.kind.topup'),
            kindDailyFee: t('admin.finance.kind.dailyFee'),
            kindVoucherPurchase: t('admin.finance.kind.voucherPurchase'),
            kindDriverTransfer: t('admin.finance.kind.driverTransfer'),
            party: t('admin.finance.filter.party'),
            partyHint: t('admin.finance.filter.partyHint'),
            apply: t('admin.finance.filter.apply'),
            clear: t('admin.finance.filter.clear'),
            timezone: t('admin.finance.filter.timezone'),
            exportCsv: t('admin.finance.ledger.exportCsv'),
            exportPdf: t('admin.finance.ledger.exportPdf'),
            pdfNote: t('admin.finance.ledger.pdfNote'),
            total: t('admin.finance.ledger.total', {
              amount: t('admin.dashboard.money', {
                amount: formatMoneyMinor(locale, report.totalMinor),
              }),
            }),
            columnWhen: t('admin.finance.ledger.when'),
            columnKind: t('admin.finance.filter.kind'),
            columnFrom: t('admin.finance.ledger.fromParty'),
            columnTo: t('admin.finance.ledger.toParty'),
            columnAmount: t('admin.finance.exceptions.amount'),
            empty: t('admin.finance.ledger.empty'),
            capped: t('admin.finance.ledger.capped', { count: 100 }),
          }}
        />
      ) : null}
    </div>
  );
}
