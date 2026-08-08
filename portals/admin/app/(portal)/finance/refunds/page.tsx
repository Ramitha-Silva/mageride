import Link from 'next/link';

import { Button, Field, Select, StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  REFUND_SOURCES,
  REFUND_STATUSES,
  REFUNDS_PATH,
  refundSearch,
  refundSelection,
  type RefundQueueRow,
} from '@/api/finance';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { findRefundTarget, refundRows, type RenderContext } from '@/components/finance/model';
import { RaiseRefundForm } from '@/components/finance/RaiseRefundForm';
import { RefundQueueTable } from '@/components/finance/RefundQueueTable';
import { financeTabs } from '@/components/finance/tabs';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { formatMoneyMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import { holdsGrant } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-006 · the Refunds tab** — E-05's queue and its one decision.
 *
 * ## The queue reaches a Support CSR; the form does not
 *
 * URD §2.3's Refunds row is the only one whose CSR cell (`◐ raise/recommend`)
 * opens a screen while withholding its button, against Finance's `✅
 * approve/execute`. So both roles' nav manifests carry `refunds` and both land
 * here — and `RaiseRefundForm` is drawn only for a caller whose own session says
 * they hold `Refunds · Write`. That is admin-bff's evaluation read back, not a
 * role check (see `holdsGrant`), and admin-bff re-decides on the `POST` regardless.
 *
 * C107's ticket pane links here for exactly this reason: a `daily_fee_refund` or
 * `driver_qr_dispute` ticket is already on Finance's pile, and the CSR's hand-off
 * is a link rather than a control that moves money.
 *
 * ## Two populations, one queue
 *
 * `source=refund` is a `fares.refunds` row awaiting settlement; `source=overpaid`
 * is a payment §11.14 moved to `Overpaid` that **nobody has raised a refund for**.
 * The second is the R-19 failure this screen exists to catch, so the source filter
 * defaults to neither and the column names which is which.
 */

export const dynamic = 'force-dynamic';

const SOURCE_LABEL = {
  refund: 'admin.finance.refund.raised',
  overpaid: 'admin.finance.refund.overpaid',
} as const;

export default async function RefundsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = refundSelection(params);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  const tabs = financeTabs(session?.menu ?? [], 'refunds');
  const canRaise = session ? holdsGrant(session, 'refunds', 'write') : false;

  let rows: readonly RefundQueueRow[] = [];
  let problem: ProblemDetails | null = null;

  try {
    rows = await read<RefundQueueRow[]>({
      path: REFUNDS_PATH,
      searchParams: refundSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const target = findRefundTarget(rows, selection.paymentId);
  const filtered = Boolean(selection.source ?? selection.status);

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

      {selection.raised ? (
        <p
          role="status"
          className="rounded-card border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {t('admin.finance.refund.done', { status: selection.raised })}{' '}
          {t('admin.audit.recorded', { action: 'REFUND_ISSUED' })}
        </p>
      ) : null}

      <form
        method="get"
        action="/finance/refunds"
        className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
      >
        <Field label={t('admin.finance.refund.source')} className="w-[200px]">
          <Select name="source" defaultValue={selection.source ?? ''}>
            <option value="">{t('admin.finance.refund.sourceAll')}</option>
            {REFUND_SOURCES.map((source) => (
              <option key={source} value={source}>
                {t(SOURCE_LABEL[source])}
              </option>
            ))}
          </Select>
        </Field>

        <Field
          label={t('admin.finance.refund.status')}
          hint={t('admin.finance.refund.statusHint')}
          className="w-[200px]"
        >
          <Select name="status" defaultValue={selection.status ?? ''}>
            <option value="">{t('admin.finance.refund.statusAll')}</option>
            {REFUND_STATUSES.map((status) => (
              <option key={status} value={status}>
                {status}
              </option>
            ))}
          </Select>
        </Field>

        <Button type="submit" size="compact">
          {t('admin.finance.filter.apply')}
        </Button>

        {filtered ? (
          <Link
            href="/finance/refunds"
            className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
          >
            {t('admin.finance.filter.clear')}
          </Link>
        ) : null}

        <span className="flex-1" />

        <StatusPill tone="warning" className="mb-xs">
          {t('admin.finance.refund.queueTotal', { count: rows.length })}
        </StatusPill>
      </form>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <RefundQueueTable
        rows={refundRows(rows, selection, context)}
        canRaise={canRaise}
        labels={{
          heading: t('admin.finance.refund.heading'),
          caption: t('admin.finance.refund.caption'),
          note: canRaise
            ? t('admin.finance.refund.note')
            : t('admin.finance.refund.readOnlyNote'),
          columnSource: t('admin.finance.refund.source'),
          columnPassenger: t('admin.finance.refund.passenger'),
          columnPayment: t('admin.finance.refund.payment'),
          columnAmount: t('admin.finance.exceptions.amount'),
          columnStatus: t('admin.finance.refund.status'),
          columnRequested: t('admin.finance.refund.requested'),
          columnAction: t('admin.finance.column.action'),
          raise: t('admin.finance.refund.raise'),
          ofPayment: t('admin.finance.refund.ofPayment'),
          empty: t('admin.finance.refund.empty'),
        }}
      />

      {canRaise ? (
        <RaiseRefundForm
          paymentId={selection.paymentId ?? ''}
          defaultKind={target?.source === 'overpaid' ? 'overpaid_reversal' : 'full'}
          ceiling={
            target
              ? t('admin.dashboard.money', {
                  amount: formatMoneyMinor(locale, target.paymentAmountMinor),
                })
              : null
          }
          targetMissing={Boolean(selection.paymentId) && target === null}
          labels={{
            heading: t('admin.finance.refund.raiseHeading'),
            payment: t('admin.finance.refund.payment'),
            paymentHint: t('admin.finance.refund.paymentHint'),
            kind: t('admin.finance.refund.kind'),
            kindFull: t('admin.finance.refund.kindFull'),
            kindPartial: t('admin.finance.refund.kindPartial'),
            kindOverpaid: t('admin.finance.refund.kindOverpaid'),
            amount: t('admin.finance.refund.amount'),
            amountHint: t('admin.finance.refund.amountHint'),
            reasonCode: t('admin.finance.refund.reasonCode'),
            reasonCodeHint: t('admin.finance.refund.reasonCodeHint'),
            submit: t('admin.finance.refund.submit'),
            working: t('admin.finance.refund.working'),
            audit: t('admin.audit.notice'),
            ceiling: t('admin.finance.refund.ceiling'),
            notInQueue: t('admin.finance.refund.notInQueue'),
          }}
        />
      ) : null}
    </div>
  );
}
