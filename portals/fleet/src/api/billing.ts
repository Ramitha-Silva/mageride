import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * The fleet wallet and the consolidated monthly invoice, transcribed from
 * `backend/contracts/fleet-billing.yaml`.
 *
 * C114 added the two reads SCR-FP-003's "Wallet & next invoice" card makes.
 * **Δ C115 completes the surface for SCR-FP-010**: the invoice detail with its
 * per-vehicle breakdown, the CSV/PDF export, the receipt, the Pay verb and the
 * three top-up routes. The dashboard card still spends nothing — its button is a
 * link to `/billing`, because a payment session belongs on the screen that owns
 * it.
 *
 * ## Every route here is the Owner's, on reads as well as writes
 *
 * `fleet-billing.yaml`'s preamble says it outright and `FleetBillingAccessFilter`
 * enforces it on the group rather than per route: **Owner sub-role and an APPROVED
 * organisation**, or `403 fleet-role-insufficient` / `403 fleet-not-approved`.
 * That is stricter than fleet-svc, where the map and the analytics are open to a
 * Viewer and to a pending organisation — US-13.A5 gives billing to the Owner and
 * takes it from the Manager in the same sentence, and a pending organisation "has
 * no approved vehicles, so it has no charges and no invoice".
 *
 * `canReadBilling()` in `@/server/access` is that gate on this side, and the card
 * is checked against it **before** it reads anything: a Manager's dashboard is not
 * a Manager's dashboard with a 403 on it.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `GET /v1/fleets/{own id}/wallet` — balance, outstanding and recent movements. */
export const FLEET_WALLET = '/wallet';

/** `GET /v1/fleets/{own id}/billing` — invoices, newest month first. */
export const FLEET_BILLING = '/billing';

/** `GET …/billing/{invoiceId}` — one invoice **with its per-vehicle lines** (Δ C060). */
export function invoiceTarget(invoiceId: string): string {
  return `${FLEET_BILLING}/${invoiceId}`;
}

/** `GET …/billing/{invoiceId}/export?format=csv|pdf` — SCR-FP-010's Download. */
export function invoiceExportTarget(invoiceId: string): string {
  return `${invoiceTarget(invoiceId)}/export`;
}

/** `GET …/billing/{invoiceId}/receipt` — US-13.10b, and `404` unless `PAID`. */
export function invoiceReceiptTarget(invoiceId: string): string {
  return `${invoiceTarget(invoiceId)}/receipt`;
}

/** `POST …/billing/{invoiceId}/pay` — settle from the fleet wallet now. */
export function invoicePayTarget(invoiceId: string): string {
  return `${invoiceTarget(invoiceId)}/pay`;
}

/** `POST /v1/fleets/{own id}/wallet/topup` — US-13.10b. */
export const FLEET_WALLET_TOPUP = '/wallet/topup';

/** `GET …/wallet/topup/{topupId}` — the poll over D6' §7.1's 90-second window. */
export function topupTarget(topupId: string): string {
  return `${FLEET_WALLET_TOPUP}/${topupId}`;
}

/* ---------------------------------------------------------------------------
 * Shapes
 * ------------------------------------------------------------------------ */

/** `fleet-billing.yaml#/components/schemas/FleetWallet`. */
export interface FleetWallet {
  /** From `billing.accounts`, the ledger master (§10) — never the dispatch mirror. */
  readonly balanceMinor: number;
  /** Σ of this organisation's `DUE` and `OVERDUE` invoices. */
  readonly outstandingMinor: number;
  /**
   * `balanceMinor − outstandingMinor`, and **signed**: "an organisation that owes
   * more than it holds is a state this screen has to draw", so it is rendered as
   * the negative number it is rather than floored at zero.
   */
  readonly availableMinor: number;
  /** `LKR`, always. */
  readonly currency: string;
  readonly updatedAt?: string;
  readonly movements: readonly FleetWalletMovement[];
}

/** `fleet-billing.yaml#/components/schemas/FleetWalletMovement`. */
export interface FleetWalletMovement {
  readonly entryId: string;
  /** `topup`, `fleet_invoice` or `adjustment`. */
  readonly kind: string;
  /** Signed as posted — a settlement is negative. */
  readonly amountMinor: number;
  readonly balanceAfterMinor: number;
  readonly description?: string;
  readonly ts: string;
}

/** `fleet-billing.yaml#/components/schemas/FleetInvoice.status`. */
export type FleetInvoiceStatus = 'FREE' | 'DUE' | 'PAID' | 'OVERDUE';

/** `fleet-billing.yaml#/components/schemas/FleetInvoice`. */
export interface FleetInvoice {
  readonly invoiceId: string;
  /** Always the first of the Colombo month. */
  readonly periodMonth: string;
  readonly periodMonthTzAt?: string;
  /**
   * Per-vehicle lines on this invoice. **Mode A vehicles are free and are never a
   * line**, so a Mode-A-only organisation reads zero here (AL-03).
   */
  readonly vehicleCount?: number;
  readonly amountMinor: number;
  readonly currency: string;
  readonly status: FleetInvoiceStatus;
  readonly dueAt?: string;
  readonly overdueAt?: string;
  readonly settledAt?: string;
  readonly journalEntryId?: string;
}

export interface FleetInvoicePage {
  readonly items: readonly FleetInvoice[];
  readonly cursor: string | null;
  readonly hasMore: boolean;
}

/**
 * `fleet-billing.yaml#/components/schemas/FleetInvoiceLine` — one vehicle's month.
 *
 * **A snapshot taken at generation**, not a join: "the plate and the vehicle type
 * are the ones that were billed, not the vehicle's current ones. A vehicle can be
 * re-plated or leave the organisation, and a settled invoice must not change under
 * it." So the breakdown is rendered from the line and never from the roster.
 *
 * There is **no `mode` on it, and there could not be**: a line exists only for a
 * charge `billing.monthly_subscriptions` raised, and that table carries Mode B
 * rows only (AL-03). `status` is `FREE` for a vehicle's first month on the
 * platform (D5' §2.1), which is worth zero and still a line.
 */
export interface FleetInvoiceLine {
  readonly vehicleId: string;
  readonly registrationNumber: string;
  readonly vehicleType: string;
  readonly amountMinor: number;
  readonly currency: string;
  readonly status: 'FREE' | 'DUE';
}

/** `fleet-billing.yaml#/components/schemas/FleetInvoiceDetail`. */
export interface FleetInvoiceDetail {
  readonly invoice: FleetInvoice;
  readonly lines: readonly FleetInvoiceLine[];
  /**
   * Σ of `lines`, computed by the service on the way out. It equals
   * `invoice.amountMinor` — "and it is returned rather than assumed **so a client
   * can check rather than trust**", which is what {@link invoiceSummary} does.
   */
  readonly lineSumMinor: number;
}

/** `fleet-billing.yaml#/components/schemas/FleetInvoiceReceipt` — a settled invoice only. */
export interface FleetInvoiceReceipt {
  readonly invoiceId: string;
  readonly fleetId: string;
  readonly fleetName: string;
  readonly periodMonth: string;
  readonly amountMinor: number;
  readonly currency: string;
  readonly vehicleCount?: number;
  readonly settledAt: string;
  /** The balanced double-entry record the settlement wrote (D-09). */
  readonly journalEntryId: string;
}

/* ---------------------------------------------------------------------------
 * The top-up rails (US-13.10b, AL-05)
 * ------------------------------------------------------------------------ */

/**
 * `topupFleetWallet`'s `method` enum — **two values, and bank transfer is not one
 * of them** (AL-05).
 *
 * `ck_fleet_topups_method` (migration 1108) refuses anything else, "so the
 * database rejects the row rather than a code review rejecting the request".
 *
 * The wireframe draws three rows — Card, OnePay, LankaQR — and they are **two
 * rails**: OnePay *is* the card rail (`Onepay:ApiKey` unset ⇒ "the card rail
 * answers 503"), so a card is paid on OnePay's hosted page rather than on a third
 * method the platform does not have. The screen says that in words instead of
 * offering an option that would post nowhere.
 */
export const TOPUP_METHODS = ['onepay', 'lankaqr'] as const;

export type TopupMethod = (typeof TOPUP_METHODS)[number];

export function isTopupMethod(value: string): value is TopupMethod {
  return (TOPUP_METHODS as readonly string[]).includes(value);
}

/**
 * `FleetBilling:MinTopupMinor` / `MaxTopupMinor` — Rs 30 to Rs 1,000,000.
 *
 * **No spec behind either**, which is exactly why they are restated here: the
 * service answers `400 invalid-amount` outside the pair, and a form that did not
 * know them would send an operator's Rs 10 to a gateway that was never going to
 * open a session for it. `test/billing.test.ts` pins both against
 * `FleetBillingOptions.cs`.
 */
export const MIN_TOPUP_MINOR = 3_000;
export const MAX_TOPUP_MINOR = 100_000_000;

/** D6' §7.1's window a `Pending` session is polled over (`TopupPendingWindow`). */
export const TOPUP_PENDING_WINDOW_SECONDS = 90;

/** `fleet-billing.yaml#/components/schemas/FleetTopup`. */
export interface FleetTopup {
  readonly topupId: string;
  readonly state: 'Pending' | 'Succeeded' | 'Failed';
  readonly amountMinor: number;
  readonly currency: string;
  readonly method: TopupMethod;
  /** OnePay's hosted page. **Present on the initiate only** — never on the poll. */
  readonly redirectUrl?: string;
  readonly sessionToken?: string;
  /** AL-15's "Pay" deep link into the bank app. Initiate only. */
  readonly paymentLink?: string;
  /** The EMVCo fallback, only where the deployment has an acquirer template. */
  readonly qrPayload?: string;
  readonly createdAt: string;
  /** True once a `Pending` session has outlived the 90-second window. */
  readonly expired?: boolean;
}

/**
 * `format` on `exportFleetInvoice`. The CSV is what an accounts department
 * reconciles — it "prints money twice, rupees and integer minor units, because a
 * spreadsheet's floating-point sum must never be the authority on somebody's
 * bill" — and the PDF is what they file.
 */
export const INVOICE_EXPORT_FORMATS = ['csv', 'pdf'] as const;

export type InvoiceExportFormat = (typeof INVOICE_EXPORT_FORMATS)[number];

export function isInvoiceExportFormat(value: string): value is InvoiceExportFormat {
  return (INVOICE_EXPORT_FORMATS as readonly string[]).includes(value);
}

/* ---------------------------------------------------------------------------
 * The wireframe's invoice table, and the fence it draws
 * ------------------------------------------------------------------------ */

/** One row of SCR-FP-010's Item / Qty / Rate / Amount table. */
export interface InvoiceSummaryRow {
  readonly key: 'mode-b' | 'mode-b-free' | 'mode-a';
  readonly qty: number;
  /** `null` means the row is free — the sketch's "Free" in the Rate column. */
  readonly rateMinor: number | null;
  /** True when the charged lines do not all carry the same per-vehicle amount. */
  readonly mixedRate: boolean;
  readonly amountMinor: number;
}

export interface InvoiceSummary {
  readonly rows: readonly InvoiceSummaryRow[];
  /** Σ of the rows above — which is Σ of the lines, because only lines can charge. */
  readonly totalMinor: number;
  /** Whether Σ lines, `lineSumMinor` and `invoice.amountMinor` are one number. */
  readonly reconciles: boolean;
}

/**
 * The wireframe's invoice table, built from the invoice's own lines.
 *
 * ## The Mode A row is the fence, and it comes from somewhere else
 *
 * The sketch draws "Mode A vehicles · 88 · Free · Rs 0", and **no such line
 * exists on any invoice**. `fleet-billing.yaml` says so in its header: "there is
 * no `mode` anywhere in this document, because a line exists only for a charge
 * `billing.monthly_subscriptions` raised and that table carries Mode B rows only"
 * — the exclusion is held by an absence, twice over, and a Mode-A-only
 * organisation gets a `FREE` invoice with zero lines.
 *
 * So the count in that row is **today's roster**, not the invoice, and it
 * contributes nothing to the total. The screen's caption says which of the two it
 * is; without that, an operator reconciling a six-month-old invoice against a
 * fleet that has since grown would find a number that moved.
 *
 * ## The total is Σ of the lines, and the screen checks rather than trusts
 *
 * `lineSumMinor` is the service's own Σ and `invoice.amountMinor` is the figure
 * the wallet is debited. All three are compared, and `reconciles` is false if any
 * pair disagrees — which the card renders as a warning rather than hiding behind
 * a header, the same call `InvoiceCsv` makes for its own TOTAL row.
 */
export function invoiceSummary(
  detail: FleetInvoiceDetail,
  modeAVehicleCount: number,
): InvoiceSummary {
  const charged = detail.lines.filter((line) => line.status !== 'FREE' || line.amountMinor > 0);
  const free = detail.lines.filter((line) => line.status === 'FREE' && line.amountMinor === 0);

  const chargedMinor = charged.reduce((sum, line) => sum + line.amountMinor, 0);
  const rates = new Set(charged.map((line) => line.amountMinor));

  const rows: InvoiceSummaryRow[] = [
    {
      key: 'mode-b',
      qty: charged.length,
      rateMinor: rates.size === 1 ? [...rates][0]! : null,
      mixedRate: rates.size > 1,
      amountMinor: chargedMinor,
    },
  ];

  // D5' §2.1's free first month. Drawn only when there is one, because a row of
  // zeros on every invoice would read as a rule rather than as this month's fact.
  if (free.length > 0) {
    rows.push({ key: 'mode-b-free', qty: free.length, rateMinor: null, mixedRate: false, amountMinor: 0 });
  }

  rows.push({
    key: 'mode-a',
    qty: modeAVehicleCount,
    rateMinor: null,
    mixedRate: false,
    amountMinor: 0,
  });

  const totalMinor = rows.reduce((sum, row) => sum + row.amountMinor, 0);
  const lineSum = detail.lines.reduce((sum, line) => sum + line.amountMinor, 0);

  return {
    rows,
    totalMinor,
    reconciles:
      totalMinor === lineSum &&
      lineSum === detail.lineSumMinor &&
      detail.lineSumMinor === detail.invoice.amountMinor,
  };
}

/**
 * Whether the Pay button does anything.
 *
 * `POST …/billing/{id}/pay` answers **`409 invoice-not-payable`** for a `FREE` or
 * already-`PAID` invoice — "a FREE invoice has a zero total and no journal entry
 * could balance, so this is a state rather than a transient failure". Both are
 * knowable before the press, so the button is not drawn for either; a wallet that
 * cannot cover the total is **not** one of them, because that is an amount to top
 * up and the operator has to be able to try.
 */
export function isPayable(invoice: FleetInvoice): boolean {
  return invoice.status === 'DUE' || invoice.status === 'OVERDUE';
}

/** Whether a receipt exists — `getFleetInvoiceReceipt` is `404` unless `PAID`. */
export function hasReceipt(invoice: FleetInvoice): boolean {
  return invoice.status === 'PAID' && Boolean(invoice.journalEntryId);
}

/**
 * How many months SCR-FP-010 lists.
 *
 * One page. The cursor is real and the history is unbounded, but an operator opens
 * this screen about *this* month and reconciles last year's from the CSV; a
 * console that paged through five years of invoices would be a ledger viewer
 * nobody asked for. Twelve is a financial year plus the month being billed.
 */
export const BILLING_PAGE_LIMIT = 12;

/**
 * How many wallet movements the statement shows.
 *
 * `GET …/wallet` takes `limit`, capped by `FleetBilling:MaxPageSize` (50). The
 * card is a statement, not a ledger export — a top-up, a settlement and the
 * balance they produced.
 */
export const WALLET_MOVEMENT_LIMIT = 10;

/* ---------------------------------------------------------------------------
 * "Next consolidated invoice"
 * ------------------------------------------------------------------------ */

/**
 * The invoice this organisation settles next — or `null` when it owes nothing.
 *
 * **There is no projection route and this does not invent one.** `web_fleet.html`
 * sketches "Next monthly invoice Rs 69,000 · 230 × Rs 300"; nothing on any
 * contract publishes the per-vehicle monthly rate or forecasts a month that has
 * not been run, and `FleetBillingRunner` raises an invoice from the vehicles that
 * were actually billable when it ran. What *is* a fact is the open invoice, so
 * that is what the card names, with `wallet.outstandingMinor` beside it as the sum
 * of every open month.
 *
 * **Oldest first, and `OVERDUE` ahead of `DUE`.** `GET …/billing` answers newest
 * month first, which is the wrong end for this question: an organisation with two
 * open months settles the older one, and it is the older one dunning is chasing.
 * Taking the first row of the response would name the month just raised and leave
 * the overdue one off the screen entirely.
 */
export function nextPayableInvoice(
  invoices: readonly FleetInvoice[] | undefined,
): FleetInvoice | null {
  const open = (invoices ?? []).filter(
    (invoice) => invoice.status === 'DUE' || invoice.status === 'OVERDUE',
  );

  return (
    [...open].sort((a, b) => {
      if (a.status !== b.status) return a.status === 'OVERDUE' ? -1 : 1;
      return a.periodMonth.localeCompare(b.periodMonth);
    })[0] ?? null
  );
}

export interface InvoiceStatusView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

/**
 * The invoice chip.
 *
 * `FREE` is neutral rather than green: a zero invoice is not a paid one, it is a
 * month in which every vehicle was in its first month or the organisation ran
 * Mode A only (D5' §2.1, AL-03).
 */
export function invoiceStatusView(status: FleetInvoiceStatus): InvoiceStatusView {
  switch (status) {
    case 'PAID':
      return { tone: 'success', labelKey: 'fleet.billing.status.paid' };
    case 'OVERDUE':
      return { tone: 'error', labelKey: 'fleet.billing.status.overdue' };
    case 'DUE':
      return { tone: 'warning', labelKey: 'fleet.billing.status.due' };
    default:
      return { tone: 'neutral', labelKey: 'fleet.billing.status.free' };
  }
}

/**
 * How many invoices the dashboard card reads.
 *
 * One small page. The card names a single invoice and the wallet already carries
 * the total outstanding, so reading years of history to find it would be a screen
 * paying for a list it never renders. Twelve months is enough that the oldest open
 * one is inside the window for any organisation that has been paying at all.
 */
export const DASHBOARD_INVOICE_LIMIT = 12;
