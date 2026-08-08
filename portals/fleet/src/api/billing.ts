import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * The fleet wallet and the consolidated monthly invoice, transcribed from
 * `backend/contracts/fleet-billing.yaml` — **only the two reads SCR-FP-003's
 * "Wallet & next invoice" card makes**.
 *
 * SCR-FP-010 is C115's screen and owns the rest of this surface: the invoice
 * history, the per-vehicle line breakdown, the CSV/PDF export, the receipt, the
 * Pay verb and the three top-up routes. They belong in this file when that
 * component lands; C114 adds none of them, because a dashboard card that could
 * spend an organisation's wallet would be a second billing screen nobody reviewed
 * as one. The card's own button is a **link to `/billing`**.
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
