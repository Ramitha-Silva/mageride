import { read } from '@/api/client';
import {
  FINANCE_PAGE_SIZE,
  PAYOUT_BATCHES_PATH,
  PAYOUTS_PATH,
  RECONCILIATION_PATH,
  reconciliationSearch,
  reconciliationSelection,
  SETTLEMENT_EXCEPTIONS_PATH,
  type CursorPage,
  type Payout,
  type PayoutBatch,
  type SettlementException,
  type SettlementSummary,
} from '@/api/finance';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ExceptionQueueTable } from '@/components/finance/ExceptionQueueTable';
import {
  exceptionRows,
  payoutBatchRows,
  payoutRows,
  settlementRails,
  settlementTotals,
  type RenderContext,
} from '@/components/finance/model';
import { PayoutsPanel } from '@/components/finance/PayoutsPanel';
import { SettlementCard } from '@/components/finance/SettlementCard';
import { SettlementFilter } from '@/components/finance/SettlementFilter';
import { financeTabs } from '@/components/finance/tabs';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-006 · `finance`, the Gateway-settlement tab** — OnePay/LankaQR
 * settlement against the ledger, its exception queue, and (behind `?view=payouts`)
 * AL-58's weekly payout run.
 *
 * ## Three reads, three independent failures
 *
 * The summary, the exception queue and the payout tables are separate calls and
 * each is caught on its own. A wallet-svc read that fails must not take the
 * exception queue with it, and — the case this shape exists for — the payout
 * tables are answered by a service the gateway does not currently route (see
 * below), so on a live deployment they are the read that fails while the other two
 * are fine.
 *
 * ## Payouts is a view rather than a card, and that is why
 *
 * `payout.yaml` names SCR-AP-006 on `listPayouts` / `listPayoutBatches`, and
 * `Payout.Api` is built (C133) — but `gateway-routes.json` has **no payout-svc
 * cluster at all**, so `/v1/admin/payouts` falls through to admin-bff's Order 90
 * catch-all, which maps no such route. Until C008 adds the cluster the two reads
 * answer 404. Drawing them as cards on the screen every Finance Officer opens
 * first would put a permanent error panel in front of a working reconciliation;
 * putting them behind a tab means the failure appears when somebody asks for
 * payouts, which is both honest and true. Raised in the C108 handoff.
 */

export const dynamic = 'force-dynamic';

export default async function ReconciliationPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = reconciliationSelection(params);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  const tabs = financeTabs(session?.menu ?? [], 'settlement');

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

      {selection.view === 'payouts' ? (
        <PayoutsView context={context} />
      ) : (
        <SettlementView selection={selection} context={context} />
      )}
    </div>
  );
}

async function SettlementView({
  selection,
  context,
}: {
  selection: ReturnType<typeof reconciliationSelection>;
  context: RenderContext;
}) {
  const { t } = context;

  let summary: SettlementSummary | null = null;
  let summaryProblem: ProblemDetails | null = null;

  try {
    summary = await read<SettlementSummary>({
      path: RECONCILIATION_PATH,
      searchParams: reconciliationSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    summaryProblem = error.problem;
  }

  let exceptions: readonly SettlementException[] = [];
  let exceptionProblem: ProblemDetails | null = null;

  try {
    exceptions = await read<SettlementException[]>({
      path: SETTLEMENT_EXCEPTIONS_PATH,
      searchParams: {
        ...(selection.kind ? { kind: selection.kind } : {}),
        limit: FINANCE_PAGE_SIZE,
      },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    exceptionProblem = error.problem;
  }

  // The rail filter narrows the queue as well as the table, so "Investigate" on a
  // rail lands on that rail's exceptions. `?method=` is not a parameter of the
  // exceptions route, so it is applied to the rows admin-bff answered rather than
  // sent — the one place on this screen a filter is not the server's.
  const visible = selection.method
    ? exceptions.filter((row) => row.method === selection.method)
    : exceptions;

  return (
    <>
      <SettlementFilter
        selection={selection}
        labels={{
          from: t('admin.finance.filter.from'),
          to: t('admin.finance.filter.to'),
          method: t('admin.finance.filter.method'),
          methodAll: t('admin.finance.filter.methodAll'),
          onepay: t('admin.finance.method.onepay'),
          lankaqr: t('admin.finance.method.lankaqr'),
          apply: t('admin.finance.filter.apply'),
          clear: t('admin.finance.filter.clear'),
          timezone: t('admin.finance.filter.timezone'),
        }}
      />

      {summaryProblem ? <ProblemPanel problem={summaryProblem} /> : null}

      {summary ? (
        <SettlementCard
          totals={settlementTotals(summary, context)}
          rails={settlementRails(summary.days, context)}
          labels={{
            heading: t('admin.finance.settlement.heading'),
            caption: t('admin.finance.settlement.caption'),
            window: t('admin.finance.settlement.window'),
            gateway: t('admin.finance.settlement.gateway'),
            sessions: t('admin.finance.settlement.sessions'),
            settled: t('admin.finance.settlement.settled'),
            posted: t('admin.finance.settlement.posted'),
            variance: t('admin.finance.settlement.variance'),
            action: t('admin.finance.column.action'),
            investigate: t('admin.finance.settlement.investigate'),
            empty: t('admin.finance.settlement.empty'),
            noBankTransfer: t('admin.finance.settlement.noBankTransfer'),
            totals: t('admin.finance.settlement.ledgerNote'),
          }}
        />
      ) : null}

      {exceptionProblem ? <ProblemPanel problem={exceptionProblem} /> : null}

      <ExceptionQueueTable
        rows={exceptionRows(visible, context)}
        labels={{
          heading: t('admin.finance.exceptions.heading'),
          caption: t('admin.finance.exceptions.caption'),
          note: t('admin.finance.exceptions.note'),
          kind: t('admin.finance.exceptions.kind'),
          gateway: t('admin.finance.settlement.gateway'),
          driver: t('admin.finance.exceptions.driver'),
          amount: t('admin.finance.exceptions.amount'),
          posted: t('admin.finance.settlement.posted'),
          opened: t('admin.finance.exceptions.opened'),
          reference: t('admin.finance.exceptions.reference'),
          empty: t('admin.finance.exceptions.empty'),
        }}
      />
    </>
  );
}

async function PayoutsView({ context }: { context: RenderContext }) {
  const { t } = context;

  let batches: readonly PayoutBatch[] = [];
  let payouts: readonly Payout[] = [];
  let problem: ProblemDetails | null = null;

  try {
    const [batchPage, payoutPage] = await Promise.all([
      read<CursorPage<PayoutBatch>>({
        path: PAYOUT_BATCHES_PATH,
        searchParams: { limit: FINANCE_PAGE_SIZE },
      }),
      read<CursorPage<Payout>>({
        path: PAYOUTS_PATH,
        searchParams: { limit: FINANCE_PAGE_SIZE },
      }),
    ]);

    batches = batchPage.items;
    payouts = payoutPage.items;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  return (
    <>
      {problem ? <ProblemPanel problem={problem} /> : null}

      <PayoutsPanel
        batches={payoutBatchRows(batches, context)}
        payouts={payoutRows(payouts, context)}
        labels={{
          heading: t('admin.finance.payouts.heading'),
          note: t('admin.finance.payouts.note'),
          batchesHeading: t('admin.finance.payouts.batchesHeading'),
          batchesCaption: t('admin.finance.payouts.batchesCaption'),
          batchRun: t('admin.finance.payouts.run'),
          batchStatus: t('admin.finance.payouts.status'),
          batchInstructions: t('admin.finance.payouts.instructions'),
          batchTotal: t('admin.finance.payouts.total'),
          batchCompleted: t('admin.finance.payouts.completed'),
          batchesEmpty: t('admin.finance.payouts.batchesEmpty'),
          instructionsCaption: t('admin.finance.payouts.instructionsCaption'),
          columnDriver: t('admin.finance.payouts.driver'),
          columnAmount: t('admin.finance.exceptions.amount'),
          columnStatus: t('admin.finance.payouts.status'),
          columnAccount: t('admin.finance.payouts.account'),
          columnCreated: t('admin.finance.payouts.created'),
          columnSettled: t('admin.finance.payouts.settled'),
          instructionsEmpty: t('admin.finance.payouts.instructionsEmpty'),
          noRetry: t('admin.finance.payouts.noRetry'),
        }}
      />
    </>
  );
}
