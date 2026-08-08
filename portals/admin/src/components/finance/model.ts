import type { StatusTone } from '@mageride/ui';

import {
  raiseRefundHref,
  type ExceptionKind,
  type Payout,
  type PayoutBatch,
  type RefundQueueRow,
  type RefundSelection,
  type SettlementDay,
  type SettlementException,
  type SettlementMethod,
  type SettlementSummary,
  type TransactionRow,
} from '@/api/finance';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import {
  formatBusinessDate,
  formatCount,
  formatDateTime,
  formatMoneyMinor,
  formatSignedMoneyMinor,
} from '@/i18n/format';

/**
 * SCR-AP-006's view model — the arithmetic and the vocabulary, apart from the
 * markup.
 *
 * ## Nothing here computes a variance
 *
 * `varianceMinor` is on the wire, per day and per window, because admin-bff reads
 * it off the ledger's own credit leg. Subtracting `postedMinor` from
 * `settledMinor` here would be a second implementation of the one number the
 * screen exists to show, and the first divergence between them would be invisible:
 * both would print something plausible. So the figure is forwarded and only its
 * **tone** is decided here — zero is reconciled, anything else is not.
 *
 * ## An absent `postedMinor` is not zero
 *
 * On a settled exception it is "the ledger holds nothing", which is the exception,
 * and the contract says so: "Absent is itself the exception on a settled session."
 * A `0` in that cell would read as a posted entry of no value. It renders as `—`,
 * the same rule SCR-AP-002 applies to a null delta.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

export interface PillView {
  readonly tone: StatusTone;
  readonly label: string;
}

/** `Rs {amount}` — the mark is a word and is translated, the amount is `Intl`'s. */
function money(minor: number, { t, locale }: RenderContext): string {
  return t('admin.dashboard.money', { amount: formatMoneyMinor(locale, minor) });
}

function signedMoney(minor: number, { t, locale }: RenderContext): string {
  return t('admin.dashboard.money', { amount: formatSignedMoneyMinor(locale, minor) });
}

/**
 * A variance, and whether it is the one the screen is looking for.
 *
 * Zero is not "small": it is the definition of reconciled, so it gets the success
 * tone and every other value gets the error tone. There is no warning band,
 * because there is no amount of unexplained money that is nearly fine.
 */
export function variancePill(varianceMinor: number, context: RenderContext): PillView {
  return {
    tone: varianceMinor === 0 ? 'success' : 'error',
    label: varianceMinor === 0 ? context.t('admin.finance.variance.none') : signedMoney(varianceMinor, context),
  };
}

/* ---------------------------------------------------------------------------
 * Gateway settlement
 * ------------------------------------------------------------------------ */

const METHOD_LABEL: Readonly<Record<SettlementMethod, AdminMessageKey>> = {
  onepay: 'admin.finance.method.onepay',
  lankaqr: 'admin.finance.method.lankaqr',
};

export interface SettlementRailView {
  readonly key: SettlementMethod;
  readonly method: string;
  /** Sessions the rail opened across the window. */
  readonly opened: string;
  readonly settled: string;
  readonly failed: string;
  readonly pending: string;
  readonly settledMoney: string;
  readonly postedMoney: string;
  readonly variance: PillView;
  /** The exception queue, narrowed to this rail — the wireframe's "Investigate". */
  readonly investigateHref: string;
  /**
   * The accessible name for that link.
   *
   * A table whose every row offers a control announcing "Investigate" is a table a
   * screen reader cannot act on, and the visible label has to stay short — so the
   * full sentence is built where the translator and the rail's name both are
   * (`moderation/model.ts`'s rule, restated).
   */
  readonly investigateNamed: string;
}

/**
 * The wireframe's settlement table: **one row per rail**, not one per day.
 *
 * admin-bff answers `days`, which is (business date × rail) — the grain a
 * reconciliation is worked at when a single day is in dispute. The screen the
 * wireframe draws is the window's summary, so the days are folded onto their rail
 * here. Folding is addition of integers in minor units and nothing else.
 *
 * **The variance is added up from the wire's own figure, never derived from the
 * folded totals.** Today the two agree by arithmetic — a sum of differences is the
 * difference of sums — so this is not a defence against a rounding error. It is a
 * defence against the definition changing: `varianceMinor` is what admin-bff
 * computed against the ledger's credit leg, and if that ever stops being exactly
 * `settledMinor − postedMinor` (a partially-posted session, a leg the report
 * counts differently), the screen must show the service's answer rather than
 * quietly substitute its own subtraction for it.
 */
export function settlementRails(
  days: readonly SettlementDay[],
  context: RenderContext,
): SettlementRailView[] {
  const totals = new Map<
    SettlementMethod,
    {
      opened: number;
      settled: number;
      failed: number;
      pending: number;
      settledMinor: number;
      postedMinor: number;
      varianceMinor: number;
    }
  >();

  for (const day of days) {
    const running = totals.get(day.method) ?? {
      opened: 0,
      settled: 0,
      failed: 0,
      pending: 0,
      settledMinor: 0,
      postedMinor: 0,
      varianceMinor: 0,
    };

    running.opened += day.openedCount;
    running.settled += day.settledCount;
    running.failed += day.failedCount;
    running.pending += day.pendingCount;
    running.settledMinor += day.settledMinor;
    running.postedMinor += day.postedMinor;
    running.varianceMinor += day.varianceMinor;

    totals.set(day.method, running);
  }

  return [...totals.entries()].map(([method, running]) => {
    const label = context.t(METHOD_LABEL[method]);

    return {
      key: method,
      method: label,
      opened: formatCount(context.locale, running.opened),
      settled: formatCount(context.locale, running.settled),
      failed: formatCount(context.locale, running.failed),
      pending: formatCount(context.locale, running.pending),
      settledMoney: money(running.settledMinor, context),
      postedMoney: money(running.postedMinor, context),
      variance: variancePill(running.varianceMinor, context),
      investigateHref: `/finance/reconciliation?method=${method}`,
      investigateNamed: context.t('admin.finance.settlement.investigateNamed', { gateway: label }),
    };
  });
}

export interface SettlementTotalsView {
  readonly window: string | null;
  readonly settledMoney: string;
  readonly postedMoney: string;
  readonly variance: PillView;
  readonly exceptions: string;
  readonly hasExceptions: boolean;
}

export function settlementTotals(
  summary: SettlementSummary,
  context: RenderContext,
): SettlementTotalsView {
  const from = formatBusinessDate(context.locale, summary.from);
  const to = formatBusinessDate(context.locale, summary.to);

  return {
    window: from && to ? `${from} – ${to}` : null,
    settledMoney: money(summary.settledMinor, context),
    postedMoney: money(summary.postedMinor, context),
    variance: variancePill(summary.varianceMinor, context),
    exceptions: context.t('admin.finance.exceptions.count', {
      count: formatCount(context.locale, summary.exceptionCount),
    }),
    hasExceptions: summary.exceptionCount > 0,
  };
}

const EXCEPTION_LABEL: Readonly<Record<ExceptionKind, AdminMessageKey>> = {
  'amount-mismatch': 'admin.finance.exception.amountMismatch',
  'settled-not-posted': 'admin.finance.exception.settledNotPosted',
  unsettled: 'admin.finance.exception.unsettled',
  'gateway-failed': 'admin.finance.exception.gatewayFailed',
};

export interface ExceptionRowView {
  readonly key: string;
  readonly kind: PillView;
  readonly method: string;
  readonly state: string;
  /** The driver the top-up was for. A name when admin-bff joined one, the id otherwise. */
  readonly driver: string;
  readonly amount: string;
  /** `—` where nothing was posted, which on a settled session **is** the exception. */
  readonly posted: string;
  readonly reference: string | null;
  readonly failureReason: string | null;
  readonly opened: string | null;
}

/**
 * The exception queue, **oldest first as admin-bff sent it**.
 *
 * Not re-sorted here. The contract's own note says why: "the queue is worked
 * oldest first and an operator touching a row must not move it to the back."
 * A screen that applied its own order would be the thing that moved it.
 */
export function exceptionRows(
  rows: readonly SettlementException[],
  context: RenderContext,
): ExceptionRowView[] {
  return rows.map((row) => ({
    key: row.topupId,
    kind: {
      // `amount-mismatch` is the one no retry fixes, so it is the one that reads
      // as an error rather than as work in progress.
      tone: row.kind === 'amount-mismatch' ? 'error' : 'warning',
      label: context.t(EXCEPTION_LABEL[row.kind]),
    },
    method: context.t(METHOD_LABEL[row.method]),
    state: row.state,
    driver: row.driverName?.trim() ? row.driverName.trim() : row.driverId,
    amount: money(row.amountMinor, context),
    posted: typeof row.postedMinor === 'number' ? money(row.postedMinor, context) : '—',
    reference: row.providerTransactionId ?? row.providerOrderId ?? null,
    failureReason: row.failureReason?.trim() ? row.failureReason.trim() : null,
    opened: formatDateTime(context.locale, row.createdAt),
  }));
}

/* ---------------------------------------------------------------------------
 * The wallet ledger, and the credit-transfer view of it
 * ------------------------------------------------------------------------ */

const KIND_LABEL = {
  topup: 'admin.finance.kind.topup',
  daily_fee: 'admin.finance.kind.dailyFee',
  voucher_purchase: 'admin.finance.kind.voucherPurchase',
  driver_transfer: 'admin.finance.kind.driverTransfer',
} as const satisfies Record<TransactionRow['kind'], AdminMessageKey>;

const ACCOUNT_LABEL = {
  passenger: 'admin.finance.account.passenger',
  driver: 'admin.finance.account.driver',
  fleet: 'admin.finance.account.fleet',
  platform: 'admin.finance.account.platform',
  suspense: 'admin.finance.account.suspense',
} as const satisfies Record<TransactionRow['fromAccountType'], AdminMessageKey>;

export interface TransactionRowView {
  readonly key: string;
  readonly kind: string;
  /** Who the money left. A name where the join found one, else the id, else the account type. */
  readonly from: string;
  readonly to: string;
  readonly amount: string;
  readonly description: string | null;
  readonly at: string | null;
}

function party(
  id: string | undefined,
  name: string | undefined,
  accountType: TransactionRow['fromAccountType'],
  { t }: RenderContext,
): string {
  if (name?.trim()) return name.trim();
  if (id) return id;
  // The platform's two singleton accounts have no owner by CHECK, so there is no
  // id to fall back to and the account type is the whole of the answer.
  return t(ACCOUNT_LABEL[accountType]);
}

export function transactionRows(
  rows: readonly TransactionRow[],
  context: RenderContext,
): TransactionRowView[] {
  return rows.map((row) => ({
    key: row.entryId,
    kind: context.t(KIND_LABEL[row.kind]),
    from: party(row.fromPartyId, row.fromName, row.fromAccountType, context),
    to: party(row.toPartyId, row.toName, row.toAccountType, context),
    amount: money(row.amountMinor, context),
    description: row.description?.trim() ? row.description.trim() : null,
    at: formatDateTime(context.locale, row.ts),
  }));
}

/* ---------------------------------------------------------------------------
 * Refunds (E-05, R-19)
 * ------------------------------------------------------------------------ */

export interface RefundRowView {
  readonly key: string;
  /** `refund` or `overpaid` — the two populations, named rather than merged. */
  readonly source: PillView;
  readonly paymentId: string;
  readonly rideId: string;
  readonly passenger: string;
  readonly method: string;
  readonly paymentState: string;
  readonly amount: string;
  /** What the attempt collected — the ceiling a refund cannot exceed. */
  readonly paymentAmount: string;
  readonly status: string | null;
  readonly reasonCode: string | null;
  readonly requestedAt: string | null;
  /** The raise form, aimed at this payment. `null` once a refund already exists. */
  readonly raiseHref: string | null;
}

/**
 * The queue, with the two populations distinguishable at a glance.
 *
 * An `overpaid` row has **no `refundId`** — "that is the point of it": nobody has
 * raised anything, and it is on the screen because §11.14 moved the payment to
 * `Overpaid` and R-19 says somebody has to notice. So it is the row that offers
 * the raise form, and a `refund` row that is already `Requested` or `Submitted`
 * does not: raising a second one against the same payment is what
 * `409 payment-already-settled` is for, and offering the button would be inviting
 * it.
 */
export function refundRows(
  rows: readonly RefundQueueRow[],
  selection: RefundSelection,
  context: RenderContext,
): RefundRowView[] {
  return rows.map((row) => ({
    key: row.refundId ?? `overpaid:${row.paymentId}`,
    source: {
      tone: row.source === 'overpaid' ? 'error' : 'warning',
      label: context.t(
        row.source === 'overpaid' ? 'admin.finance.refund.overpaid' : 'admin.finance.refund.raised',
      ),
    },
    paymentId: row.paymentId,
    rideId: row.rideId,
    passenger: row.passengerName?.trim() ? row.passengerName.trim() : (row.passengerId ?? '—'),
    method: row.method,
    paymentState: row.paymentState,
    amount: money(row.amountMinor, context),
    paymentAmount: money(row.paymentAmountMinor, context),
    status: row.status ?? null,
    reasonCode: row.reasonCode?.trim() ? row.reasonCode.trim() : null,
    requestedAt: formatDateTime(context.locale, row.requestedAt),
    raiseHref: row.refundId ? null : raiseRefundHref(selection, row.paymentId),
  }));
}

/**
 * The row the raise form is aimed at, so the form can state the ceiling and
 * default the kind.
 *
 * `null` when the URL names a payment this page did not answer with — a bookmark
 * from yesterday, or a filter that has since moved the row. The form says the row
 * is not in this queue rather than posting against an id nothing on screen
 * describes.
 */
export function findRefundTarget(
  rows: readonly RefundQueueRow[],
  paymentId: string | undefined,
): RefundQueueRow | null {
  if (!paymentId) return null;
  return rows.find((row) => row.paymentId === paymentId && !row.refundId) ?? null;
}

/* ---------------------------------------------------------------------------
 * Payouts (AL-58)
 * ------------------------------------------------------------------------ */

const PAYOUT_TONE: Readonly<Record<Payout['status'], StatusTone>> = {
  PENDING: 'warning',
  SUBMITTED: 'info',
  PAID: 'success',
  FAILED: 'error',
};

export interface PayoutRowView {
  readonly key: string;
  readonly driverId: string;
  readonly amount: string;
  readonly status: PillView;
  /** Last four digits of the verified account the money was sent to. */
  readonly account: string | null;
  readonly failureReason: string | null;
  readonly created: string | null;
  readonly settled: string | null;
}

export function payoutRows(rows: readonly Payout[], context: RenderContext): PayoutRowView[] {
  return rows.map((row) => ({
    key: row.payoutId,
    driverId: row.driverId,
    amount: money(row.amountMinor, context),
    status: { tone: PAYOUT_TONE[row.status], label: row.status },
    account: row.accountNoMasked?.trim() ? row.accountNoMasked.trim() : null,
    // A FAILED instruction has already had its debit reversed — the money is back
    // on the driver's wallet — so the reason is the whole of what an operator can
    // act on, and there is deliberately no retry control anywhere (C133 removed
    // the route).
    failureReason: row.failureReason?.trim() ? row.failureReason.trim() : null,
    created: formatDateTime(context.locale, row.createdAt),
    settled: formatDateTime(context.locale, row.settledAt),
  }));
}

export interface PayoutBatchView {
  readonly key: string;
  readonly runDate: string | null;
  readonly status: PillView;
  readonly instructions: string;
  readonly total: string;
  readonly completed: string | null;
}

export function payoutBatchRows(
  rows: readonly PayoutBatch[],
  context: RenderContext,
): PayoutBatchView[] {
  return rows.map((row) => ({
    key: row.batchId,
    runDate: formatBusinessDate(context.locale, row.runDate),
    status: {
      tone: row.status === 'COMPLETED' ? 'success' : row.status === 'FAILED' ? 'error' : 'warning',
      label: row.status,
    },
    instructions: formatCount(context.locale, row.instructionCount),
    total: money(row.totalMinor, context),
    completed: formatDateTime(context.locale, row.completedAt),
  }));
}
