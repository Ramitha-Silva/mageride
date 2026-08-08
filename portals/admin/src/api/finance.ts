/**
 * SCR-AP-006's wire shapes, its paths, and the state its five tabs keep in the
 * URL.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` — the six operations tagged
 * `admin-finance` — and from `backend/contracts/payout.yaml`'s `listPayouts` /
 * `listPayoutBatches`, which are the outbound half of the same screen (AL-58
 * names SCR-AP-006 on both).
 *
 * ## One screen in D2, four nav items in the manifest
 *
 * The wireframe draws one Finance page with five tabs. `AdminMenu.cs` splits it
 * into `reconciliation`, `transactions`, `refunds` and `wallet-adjustments`,
 * because they are **four different URD §2.3 rows** — a Support CSR holds
 * `◐ raise/recommend` on Refunds and nothing at all on Finance, and a single
 * screen would have to either show them the settlement figures or hide the refund
 * queue they are entitled to. So the tabs are drawn from the caller's own menu
 * (`components/finance/tabs.ts`) and each lands on the item that is gated the way
 * that tab's data is.
 *
 * ## There is no bank-transfer rail (AL-05)
 *
 * `?method=` admits `onepay` and `lankaqr` and nothing else — admin-bff answers a
 * third value with a `400` rather than an empty page, and this module's
 * {@link SETTLEMENT_METHODS} is that enum. AL-57 is why it is exhaustive rather
 * than a simplification: the two rails settle wallet **top-ups**, because neither
 * survives as a ride payment method.
 */

import type { CursorPage } from './types';

export type { CursorPage };

/* ---------------------------------------------------------------------------
 * Paths
 * ------------------------------------------------------------------------ */

/** `GET /v1/admin/finance/reconciliation` — gateway settlement against the ledger. */
export const RECONCILIATION_PATH = '/v1/admin/finance/reconciliation';

/** `GET /v1/admin/finance/reconciliation/exceptions` — the sessions that need a human. */
export const SETTLEMENT_EXCEPTIONS_PATH = '/v1/admin/finance/reconciliation/exceptions';

/** `GET` the refund queue · `POST` a refund (E-05, R-19). */
export const REFUNDS_PATH = '/v1/admin/finance/refunds';

/** `GET /v1/admin/finance/transactions` — the wallet-ledger report (US-9A.15). */
export const TRANSACTIONS_PATH = '/v1/admin/finance/transactions';

/** The same rows, rendered by admin-bff. Relayed, never re-rendered here. */
export const TRANSACTIONS_CSV_PATH = '/v1/admin/finance/transactions.csv';
export const TRANSACTIONS_PDF_PATH = '/v1/admin/finance/transactions.pdf';

/** `POST /v1/admin/drivers/wallet/{driverId}/reverse-fee` (US-14.11). */
export const WALLET_PATH = '/v1/admin/drivers/wallet';

/** `GET /v1/admin/payouts` — payout-svc's instructions, for Finance (AL-58). */
export const PAYOUTS_PATH = '/v1/admin/payouts';

/** `GET /v1/admin/payouts/batches` — the weekly run history. */
export const PAYOUT_BATCHES_PATH = '/v1/admin/payouts/batches';

export function reverseFeePath(driverId: string): string {
  return `${WALLET_PATH}/${driverId}/reverse-fee`;
}

/* ---------------------------------------------------------------------------
 * Enums, as the contracts declare them
 * ------------------------------------------------------------------------ */

/** The two gateway rails there are. **There is no bank transfer** (AL-05). */
export const SETTLEMENT_METHODS = ['onepay', 'lankaqr'] as const;

export type SettlementMethod = (typeof SETTLEMENT_METHODS)[number];

export function isSettlementMethod(value: string | undefined): value is SettlementMethod {
  return value !== undefined && (SETTLEMENT_METHODS as readonly string[]).includes(value);
}

/**
 * The four exception kinds, **derived from the row rather than stored on it** —
 * wallet-svc records no exception column, so a session that resolves itself
 * leaves the queue with nobody having to close it.
 */
export const EXCEPTION_KINDS = [
  'amount-mismatch',
  'settled-not-posted',
  'unsettled',
  'gateway-failed',
] as const;

export type ExceptionKind = (typeof EXCEPTION_KINDS)[number];

export function isExceptionKind(value: string | undefined): value is ExceptionKind {
  return value !== undefined && (EXCEPTION_KINDS as readonly string[]).includes(value);
}

/**
 * The two populations on the refund queue, which is the point of it: a `refund`
 * is a `fares.refunds` row already raised, an `overpaid` is a payment §11.14 moved
 * to `Overpaid` that **nobody has raised a refund for** — the R-19 failure the
 * queue exists to catch.
 */
export const REFUND_SOURCES = ['refund', 'overpaid'] as const;

export type RefundSource = (typeof REFUND_SOURCES)[number];

export function isRefundSource(value: string | undefined): value is RefundSource {
  return value !== undefined && (REFUND_SOURCES as readonly string[]).includes(value);
}

/** `fares.refunds.status`. */
export const REFUND_STATUSES = ['Requested', 'Submitted', 'Succeeded', 'Failed'] as const;

export type RefundStatus = (typeof REFUND_STATUSES)[number];

export function isRefundStatus(value: string | undefined): value is RefundStatus {
  return value !== undefined && (REFUND_STATUSES as readonly string[]).includes(value);
}

/** What `POST /v1/admin/finance/refunds` will raise. */
export const REFUND_KINDS = ['full', 'partial', 'overpaid_reversal'] as const;

export type RefundKind = (typeof REFUND_KINDS)[number];

export function isRefundKind(value: string | undefined): value is RefundKind {
  return value !== undefined && (REFUND_KINDS as readonly string[]).includes(value);
}

/**
 * The four `billing.journal_entries.kind` values the report admits.
 *
 * The ledger holds twelve. A report that showed all of them would put a trip
 * payment, a penalty settlement and a weekly payout under column headings that
 * describe none of the three — so admin-bff filters to four, and this is that
 * list rather than a filter the screen applies afterwards.
 */
export const TRANSACTION_KINDS = ['topup', 'daily_fee', 'voucher_purchase', 'driver_transfer'] as const;

export type TransactionKind = (typeof TRANSACTION_KINDS)[number];

export function isTransactionKind(value: string | undefined): value is TransactionKind {
  return value !== undefined && (TRANSACTION_KINDS as readonly string[]).includes(value);
}

/** `TransactionRow.fromAccountType` / `toAccountType`. */
export type AccountType = 'passenger' | 'driver' | 'fleet' | 'platform' | 'suspense';

/** `payout.yaml#/components/schemas/PayoutStatus`. */
export type PayoutStatus = 'PENDING' | 'SUBMITTED' | 'PAID' | 'FAILED';

/* ---------------------------------------------------------------------------
 * Wire shapes
 * ------------------------------------------------------------------------ */

/** One (business date, rail) pair of the reconciliation window. */
export interface SettlementDay {
  readonly businessDate: string;
  readonly method: SettlementMethod;
  readonly openedCount: number;
  readonly settledCount: number;
  readonly failedCount: number;
  readonly pendingCount: number;
  readonly settledMinor: number;
  readonly postedMinor: number;
  /** `settledMinor − postedMinor`. **Zero is what "reconciled" means.** */
  readonly varianceMinor: number;
  readonly currency: string;
}

export interface SettlementSummary {
  readonly from: string;
  readonly to: string;
  readonly settledMinor: number;
  readonly postedMinor: number;
  readonly varianceMinor: number;
  /** How many sessions are in the exception queue right now — the wireframe's tab badge. */
  readonly exceptionCount: number;
  readonly days: readonly SettlementDay[];
}

export interface SettlementException {
  readonly topupId: string;
  readonly kind: ExceptionKind;
  readonly method: SettlementMethod;
  /** The `billing.topups.state` the session is actually in. */
  readonly state: 'Pending' | 'Succeeded' | 'Failed';
  readonly driverId: string;
  readonly driverName?: string;
  readonly amountMinor: number;
  /** What the ledger holds. **Absent is itself the exception** on a settled session. */
  readonly postedMinor?: number;
  readonly currency: string;
  readonly providerTransactionId?: string;
  readonly providerOrderId?: string;
  readonly failureReason?: string;
  readonly createdAt: string;
  readonly settledAt?: string;
}

export interface RefundQueueRow {
  /** **Absent on an `overpaid` row — that is the point of it.** */
  readonly refundId?: string;
  readonly source: RefundSource;
  readonly paymentId: string;
  readonly rideId: string;
  readonly paymentState: string;
  readonly method: string;
  readonly kind?: RefundKind;
  readonly status?: RefundStatus;
  /** The refund's amount where one was raised; the payment's otherwise. */
  readonly amountMinor: number;
  /** What the attempt collected — the ceiling a refund cannot exceed. */
  readonly paymentAmountMinor: number;
  readonly currency: string;
  readonly reasonCode?: string;
  readonly providerRefundId?: string;
  readonly passengerId?: string;
  readonly passengerName?: string;
  readonly requestedAt: string;
  readonly settledAt?: string;
}

/** What `POST /v1/admin/finance/refunds` left behind. */
export interface Refund {
  readonly refundId: string;
  readonly status: RefundStatus;
  readonly amountMinor: number;
  readonly currency: string;
}

/**
 * One money **event**, not one account leg.
 *
 * `fromParty` owns the negative leg and `toParty` the positive one, so a
 * driver-to-driver transfer is one row rather than two — which is why the report
 * reads the journal and not `billing.wallet_transactions`, whose projection would
 * double the platform's transfer volume when summed.
 */
export interface TransactionRow {
  readonly entryId: string;
  readonly kind: TransactionKind;
  /** Unsigned — the magnitude that moved. The two party fields say which way. */
  readonly amountMinor: number;
  readonly currency: string;
  /** Absent for the platform's two singleton accounts, which have no owner by CHECK. */
  readonly fromPartyId?: string;
  readonly fromName?: string;
  readonly fromAccountType: AccountType;
  readonly toPartyId?: string;
  readonly toName?: string;
  readonly toAccountType: AccountType;
  readonly description?: string;
  readonly ts: string;
}

export interface TransactionsReport {
  readonly from: string;
  readonly to: string;
  /** The single kind that was filtered on, absent when all four are included. */
  readonly kind?: TransactionKind;
  readonly totalMinor: number;
  readonly items: readonly TransactionRow[];
}

/** `payout.yaml#/components/schemas/Payout` — one instruction in one weekly sweep. */
export interface Payout {
  readonly payoutId: string;
  readonly batchId: string;
  readonly driverId: string;
  /** The driver's whole balance at sweep time — no minimum, no holdback. */
  readonly amountMinor: number;
  readonly currency: string;
  readonly status: PayoutStatus;
  /** Last four digits of the verified account the money was sent to. */
  readonly accountNoMasked?: string;
  readonly failureReason?: string;
  readonly providerReference?: string;
  readonly createdAt: string;
  readonly settledAt?: string;
}

/** `payout.yaml#/components/schemas/PayoutBatch` — one weekly run. */
export interface PayoutBatch {
  readonly batchId: string;
  /** The Asia/Colombo business date of the sweep (D-13/D-38). */
  readonly runDate: string;
  readonly tzAt?: string;
  readonly status: 'RUNNING' | 'COMPLETED' | 'FAILED';
  readonly instructionCount: number;
  /** What left the platform that week — the figure Finance reconciles the statement against. */
  readonly totalMinor: number;
  readonly startedAt?: string;
  readonly completedAt?: string;
}

/** What `POST …/reverse-fee` answers with. */
export interface FeeReversal {
  readonly entryId: string;
  readonly amountMinor: number;
  readonly currency: string;
  /** Signed — a wallet may be negative (US-9A.7). */
  readonly balanceAfterMinor: number;
  /**
   * **True when the ledger key had already been used** — a double click, answered
   * with the original entry rather than a second credit. On the wire because an
   * operator who pressed twice deserves to be told the second press did nothing.
   */
  readonly replayed: boolean;
}

/* ---------------------------------------------------------------------------
 * Query state — held in the URL, like every other filter on this surface
 * ------------------------------------------------------------------------ */

/** The contract's maximum page size, and what every finance list is asked for. */
export const FINANCE_PAGE_SIZE = 100;

const BUSINESS_DATE = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Whether a value is a `_shared.yaml#/components/schemas/BusinessDate`.
 *
 * The round trip through `Date` is what rejects `2026-02-31`: the pattern alone
 * admits it, and the query string is whatever an operator pasted or a bookmark
 * preserved. Same rule as `api/dashboard.ts`, stated once per module because the
 * two screens do not share a filter.
 */
export function isBusinessDate(value: string | undefined): value is string {
  if (!value || !BUSINESS_DATE.test(value)) return false;

  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().startsWith(value);
}

const ADMIN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Whether a value is an id this portal will interpolate into an API path or send
 * as a filter.
 *
 * admin-bff routes the reversal on `{driverId:guid}`, so anything else is a 404
 * there — but it would be a 404 reached by putting a string somebody typed into a
 * path this process builds. The same guard `api/moderation.ts` states, for the
 * same reason: this screen takes ids from operators.
 */
export function isAdminId(value: string | undefined | null): value is string {
  return typeof value === 'string' && ADMIN_ID.test(value);
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

/**
 * The two panes of the reconciliation screen, in the URL.
 *
 * `payouts` is the wireframe's fourth tab and is a **separate view rather than a
 * card on the settlement page**, because payout-svc has no gateway route yet (see
 * the C108 handoff): a permanently-failing card on the screen every Finance
 * Officer opens first would read as the platform being broken, where a tab that
 * fails when it is asked for reads as the one feature that is not wired up.
 */
export const RECONCILIATION_VIEWS = ['settlement', 'exceptions', 'payouts'] as const;

export type ReconciliationView = (typeof RECONCILIATION_VIEWS)[number];

function isReconciliationView(value: string | undefined): value is ReconciliationView {
  return value !== undefined && (RECONCILIATION_VIEWS as readonly string[]).includes(value);
}

export interface ReconciliationSelection {
  readonly view: ReconciliationView;
  readonly from?: string;
  readonly to?: string;
  readonly method?: SettlementMethod;
  readonly kind?: ExceptionKind;
}

export function reconciliationSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): ReconciliationSelection {
  const view = first(params.view);
  const from = first(params.from);
  const to = first(params.to);
  const method = first(params.method);
  const kind = first(params.kind);

  return {
    view: isReconciliationView(view) ? view : 'settlement',
    // A half-chosen range is not sent: admin-bff defaults to the last 30 business
    // days (D-38), which is a window an operator can read off the response, where
    // one open end would be a validation error about a form they have not
    // finished filling in.
    ...(isBusinessDate(from) && isBusinessDate(to) ? { from, to } : {}),
    ...(isSettlementMethod(method) ? { method } : {}),
    ...(isExceptionKind(kind) ? { kind } : {}),
  };
}

/** The settlement read's query — `from`/`to`/`method`, and nothing the screen invented. */
export function reconciliationSearch(
  selection: ReconciliationSelection,
): Record<string, string> {
  return {
    ...(selection.from && selection.to ? { from: selection.from, to: selection.to } : {}),
    ...(selection.method ? { method: selection.method } : {}),
  };
}

export interface TransactionsSelection {
  readonly from?: string;
  readonly to?: string;
  readonly kind?: TransactionKind;
  readonly partyId?: string;
}

export function transactionsSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): TransactionsSelection {
  const from = first(params.from);
  const to = first(params.to);
  const kind = first(params.kind);
  const partyId = first(params.partyId);

  return {
    ...(isBusinessDate(from) && isBusinessDate(to) ? { from, to } : {}),
    ...(isTransactionKind(kind) ? { kind } : {}),
    ...(isAdminId(partyId) ? { partyId } : {}),
  };
}

/**
 * The transactions query — for the screen, for the CSV and for the PDF.
 *
 * **One function, three renderings.** admin-bff builds all three from one
 * `IFinanceService` call, so "the export matches the screen" is structural on
 * that side; this is the half of it that lives here, and the reason the export
 * route handlers take a `TransactionsSelection` rather than the raw query string.
 */
export function transactionsSearch(
  selection: TransactionsSelection,
): Record<string, string | number> {
  return {
    ...(selection.from && selection.to ? { from: selection.from, to: selection.to } : {}),
    ...(selection.kind ? { kind: selection.kind } : {}),
    ...(selection.partyId ? { partyId: selection.partyId } : {}),
    limit: FINANCE_PAGE_SIZE,
  };
}

/** `path` with this selection's query on it — what the two export links are. */
export function transactionsHref(path: string, selection: TransactionsSelection): string {
  const query = new URLSearchParams();
  if (selection.from && selection.to) {
    query.set('from', selection.from);
    query.set('to', selection.to);
  }
  if (selection.kind) query.set('kind', selection.kind);
  if (selection.partyId) query.set('partyId', selection.partyId);

  const search = query.toString();
  return search ? `${path}?${search}` : path;
}

export interface RefundSelection {
  readonly source?: RefundSource;
  readonly status?: RefundStatus;
  /** The queue row a raise form is aimed at, carried the way SCR-AP-004 aims its suspend card. */
  readonly paymentId?: string;
  /** The refund just raised, so the queue can confirm what left it. */
  readonly raised?: RefundStatus;
}

export function refundSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): RefundSelection {
  const source = first(params.source);
  const status = first(params.status);
  const paymentId = first(params.paymentId);
  const raised = first(params.raised);

  return {
    ...(isRefundSource(source) ? { source } : {}),
    ...(isRefundStatus(status) ? { status } : {}),
    ...(isAdminId(paymentId) ? { paymentId } : {}),
    ...(isRefundStatus(raised) ? { raised } : {}),
  };
}

export function refundSearch(selection: RefundSelection): Record<string, string | number> {
  return {
    ...(selection.source ? { source: selection.source } : {}),
    ...(selection.status ? { status: selection.status } : {}),
    limit: FINANCE_PAGE_SIZE,
  };
}

/** The link a queue row's "Raise a refund" control is: the form, aimed and anchored. */
export function raiseRefundHref(selection: RefundSelection, paymentId: string): string {
  const query = new URLSearchParams();
  if (selection.source) query.set('source', selection.source);
  if (selection.status) query.set('status', selection.status);
  query.set('paymentId', paymentId);

  return `/finance/refunds?${query.toString()}#raise`;
}
