import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

import { VEHICLES, type ServicePayment } from './vehicles';

/**
 * **SCR-FP-011 and SCR-FP-012** — the owner's half of Mode B: the per-vehicle
 * request queue, the subscriber roster and the per-subscriber payment ledger
 * (Epic 23, AL-23/24/25), transcribed from
 * `backend/contracts/fleet.yaml#fleet-subscriptions`.
 *
 * Nothing here calls anything. The org-relative targets are the strings a screen
 * hands `read()`/`mutate()`, which is where the caller's own `fleetId` is written
 * into a URL and the only place it ever is (`./client`).
 *
 * ## 1. AL-23 is a fence, and it is the shape of every target in this file
 *
 * "Sharing & subscription requests are **scoped per vehicle**:
 * `subscription.access_requests` and `subscription.grants` carry `vehicle_id`."
 * There is no fleet-wide queue route and no fleet-wide roster route on any
 * contract — every one of the eight proxies except the slip confirmation is
 * `…/vehicles/{vehicleId}/…`, and subscription-svc holds the same fence in its
 * repository ("there is no method on this interface that takes a fleet, an owner
 * or an account"). So a vehicle is not a filter these screens apply to a larger
 * answer; it is the only address at which an answer exists, which is why
 * `?vehicle={id}` is the screens' state rather than a convenience.
 *
 * ## 2. This money is the owner's, and never MageRide's
 *
 * AL-24 makes subscription payments a **pass-through to the fleet owner**, and
 * BR-23.10 keeps a fleet's fares off MageRide's books entirely — which is also
 * why SCR-FP-009 has no earnings column. The one place MageRide charges this
 * organisation is SCR-FP-010's monthly per-Mode-B-vehicle invoice, and these two
 * screens must never read as the same money. {@link SUBSCRIPTION_MONEY_IS_PASS_THROUGH}
 * is that rule as a constant the screens caption themselves with.
 *
 * ## 3. The muted row is a server flag, not a derivation
 *
 * `SubscriberRow.muted` is `subscription.grants.status = 'unsubscribed'` read
 * back, and AL-25 makes the row survive until the owner hard-deletes it. The
 * portal therefore never *decides* that a row is muted and never hides one: it
 * renders `muted` and draws Delete, and `deleteFleetVehicleSubscriber` answers
 * `409 conflict` for a row that is still active — "only a passenger can end their
 * own subscription".
 */

/* ---------------------------------------------------------------------------
 * Targets — every one of them per vehicle (AL-23)
 * ------------------------------------------------------------------------ */

/** `GET …/vehicles/{vehicleId}/requests` — item 15's incoming queue. */
export function vehicleRequestsTarget(vehicleId: string): string {
  return `${VEHICLES}/${vehicleId}/requests`;
}

/** `POST …/requests/{requestId}/accept` — grant + subscription, in one call. */
export function acceptRequestTarget(vehicleId: string, requestId: string): string {
  return `${vehicleRequestsTarget(vehicleId)}/${requestId}/accept`;
}

/** `POST …/requests/{requestId}/reject` — terminal; nothing is created. */
export function rejectRequestTarget(vehicleId: string, requestId: string): string {
  return `${vehicleRequestsTarget(vehicleId)}/${requestId}/reject`;
}

/** `GET …/vehicles/{vehicleId}/subscribers` — items 16 and 17's roster. */
export function vehicleSubscribersTarget(vehicleId: string): string {
  return `${VEHICLES}/${vehicleId}/subscribers`;
}

/** `DELETE …/subscribers/{subscriberId}` — item 17's hard delete. */
export function subscriberTarget(vehicleId: string, subscriberId: string): string {
  return `${vehicleSubscribersTarget(vehicleId)}/${subscriberId}`;
}

/** `PUT …/subscribers/{subscriberId}/fare` — item 16f, US-23.7. */
export function subscriberFareTarget(vehicleId: string, subscriberId: string): string {
  return `${subscriberTarget(vehicleId, subscriberId)}/fare`;
}

/** `POST …/subscribers/{subscriberId}/mark-cash` — item 16f, US-23.6. */
export function subscriberMarkCashTarget(vehicleId: string, subscriberId: string): string {
  return `${subscriberTarget(vehicleId, subscriberId)}/mark-cash`;
}

/** `GET …/subscribers/{subscriberId}/payments` — item 16i, SCR-FP-012. */
export function subscriberPaymentsTarget(vehicleId: string, subscriberId: string): string {
  return `${subscriberTarget(vehicleId, subscriberId)}/payments`;
}

/**
 * `POST /v1/fleets/{own id}/payments/{paymentId}/confirm` — item 16f.
 *
 * **The one proxy addressed by payment rather than by vehicle**, and therefore
 * the one target in this file with no `vehicleId` in it. It hangs off the fleet
 * group instead, and subscription-svc resolves the payment's own vehicle and
 * checks ownership against that (`FleetOpsEndpoints`) — so AL-23's fence is held
 * on the far side of the call rather than in the URL.
 */
export function confirmPaymentTarget(paymentId: string): string {
  return `/payments/${paymentId}/confirm`;
}

/* ---------------------------------------------------------------------------
 * Paging
 * ------------------------------------------------------------------------ */

/**
 * `_shared.yaml#/components/schemas/CursorPage` — `cursor` is `null` on the last
 * page and never omitted, "so 'last page' cannot be confused with 'field
 * missing'".
 */
export interface CursorPage<T> {
  readonly items: readonly T[];
  readonly cursor: string | null;
  readonly hasMore: boolean;
}

/**
 * `_shared.yaml`'s `limit` ceiling, asked for on all three lists.
 *
 * A roster is a vehicle's seats — a van holds fifteen and a bus fifty — so one
 * page is the whole answer in every real case, and the screens say so rather
 * than pretending otherwise when `hasMore` is true (there is no "next page"
 * control on either screen, so an unmentioned second page would be data an
 * operator could not reach).
 */
export const SUBSCRIPTION_PAGE_LIMIT = 100;

/** A year of a subscriber's months, which is what a ledger is opened to read. */
export const LEDGER_PAGE_LIMIT = 24;

/* ---------------------------------------------------------------------------
 * Item 15 — the request queue
 * ------------------------------------------------------------------------ */

/** `fleet.yaml#/components/schemas/AccessRequest.status`. */
export type AccessRequestStatus = 'pending' | 'accepted' | 'rejected';

/** `fleet.yaml#/components/schemas/AccessRequest`. */
export interface AccessRequest {
  readonly requestId: string;
  readonly vehicleId: string;
  readonly passengerId: string;
  readonly passengerName?: string;
  /** `_shared.yaml`'s `PhoneMasked` — masked by subscription-svc, never here. */
  readonly passengerMobileMasked?: string;
  readonly status: AccessRequestStatus;
  readonly createdAt: string;
}

export type AccessRequestPage = CursorPage<AccessRequest>;

/** The body `POST …/reject` takes. `reason` is optional and capped at 500. */
export interface RejectRequestBody {
  readonly reason?: string;
}

/** `fleet.yaml`'s `maxLength: 500` on the rejection reason. */
export const REJECT_REASON_MAX_LENGTH = 500;

/** What an accept produced — the grant and the subscription it started. */
export interface AcceptRequestResult {
  readonly requestId: string;
  readonly grantId: string;
  readonly subscriptionId: string;
}

/**
 * The queue as the screen shows it: **pending only**.
 *
 * `listFleetVehicleRequests` is documented as "Pending requests", but the schema
 * carries all three statuses and a decided row is not something an operator can
 * act on twice — an accepted request already has a roster row beneath it, and a
 * rejected one is terminal. Filtering here rather than trusting the description
 * means a service that later widened the answer would not put dead Accept
 * buttons on the screen.
 */
export function pendingRequests(requests: readonly AccessRequest[]): readonly AccessRequest[] {
  return requests.filter((request) => request.status === 'pending');
}

/* ---------------------------------------------------------------------------
 * Items 16 and 17 — the roster
 * ------------------------------------------------------------------------ */

/**
 * `SubscriberRow.billing` — `ModeBBilling`, which AL-51 labels "Service payment".
 *
 * The same two values `FleetVehicle.modeBBilling` carries, and deliberately the
 * same type: a subscriber's billing is the vehicle's classification as it stood
 * when the subscription started, and a screen that had two names for one enum
 * would eventually compare them.
 */
export type { ServicePayment };

/** `SubscriberRow.cycle` — US-23.8's two, and there is no third. */
export type SubscriptionCycle = 'month_first' | 'join_anniversary';

/**
 * `SubscriberRow.thisMonthStatus` — subscription-svc's verdict on the **current
 * Colombo month**, not a status the portal computes.
 *
 * `SubscriberRowResponse.ThisMonthStatusOf` folds `initiated` and "no payment row
 * at all" into `unpaid`, and says why: "a passenger who opened the pay sheet and
 * walked away has not paid, and showing the owner anything else would have them
 * stop chasing a month that never arrived". So `unpaid` here means *owed*, and
 * it carries no claim about how it will eventually be paid — see
 * {@link CASH_IS_NOT_KNOWN_IN_ADVANCE}.
 */
export type ThisMonthStatus = 'paid' | 'unpaid' | 'pending_verification';

/** `SubscriberRow.status` — the grant's own state (`subscription.grants.status`). */
export type SubscriberStatus = 'active' | 'unsubscribed';

/**
 * `fleet.yaml#/components/schemas/SubscriberRow`.
 *
 * `subscriberId` is the **grant** id, not the subscription's: subscription-svc
 * calls it "the roster row's identity — the thing an owner deletes and sets a
 * fare on — and it survives a subscription being cancelled and re-created by a
 * rejoin, which is what makes the Fleet Portal's per-subscriber ledger continuous
 * across one". Every write on this screen is addressed by it.
 */
export interface SubscriberRow {
  readonly subscriberId: string;
  readonly passengerId: string;
  readonly name?: string;
  readonly mobileMasked?: string;
  readonly billing: ServicePayment;
  readonly monthlyFareMinor?: number;
  /** `LKR`, always — `_shared.yaml` makes it a `const`. */
  readonly currency?: string;
  readonly cycle?: SubscriptionCycle;
  readonly thisMonthStatus?: ThisMonthStatus;
  readonly muted: boolean;
  readonly status: SubscriberStatus;
}

export type SubscriberPage = CursorPage<SubscriberRow>;

/** The body `PUT …/fare` takes. Integer minor units, `minimum: 0`. */
export interface SetFareBody {
  readonly monthlyFareMinor: number;
}

/** The body `POST …/mark-cash` takes. `periodMonth` defaults to the month due. */
export interface MarkCashBody {
  readonly amountMinor: number;
  readonly periodMonth?: string;
}

/**
 * Whether this subscriber's fare can be set at all.
 *
 * `SetFareAsync` answers `409 conflict` for anything else, in as many words:
 * "This subscriber has no live Paid subscription to set a fare on. A Free service
 * payment carries no fare (BR-23.8)." An unsubscribed grant has no live
 * subscription either, so both halves are checked here and the control is not
 * drawn rather than drawn and refused.
 */
export function canSetFare(row: SubscriberRow): boolean {
  return row.status === 'active' && row.billing === 'paid';
}

/**
 * Whether a cash payment can be recorded against this subscriber right now.
 *
 * `MarkCashAsync` runs `RequireCollectable` first — an inactive subscription
 * "collects nothing", and a Free one has "no fare to pay (BR-23.8)" — and then
 * refuses a month that is already paid with `409 conflict`. The first two are
 * knowable from the roster row; the third is knowable for the *current* month,
 * which is the month the button is about.
 */
export function canMarkCash(row: SubscriberRow): boolean {
  return canSetFare(row) && row.thisMonthStatus !== 'paid';
}

/**
 * Whether AL-25's hard delete applies to this row.
 *
 * The order is the passenger's first: `DeleteSubscriberAsync` answers
 * `409 conflict` for an active row because "only a passenger can end their own
 * subscription; the row becomes deletable once they have (US-4.12)". So Delete is
 * drawn on a muted row and on nothing else, which is also exactly what the
 * wireframe draws.
 */
export function canDelete(row: SubscriberRow): boolean {
  return row.muted || row.status === 'unsubscribed';
}

/** Active subscribers first, muted rows after them, each newest-first as answered. */
export function byActiveFirst(a: SubscriberRow, b: SubscriberRow): number {
  return Number(canDelete(a)) - Number(canDelete(b));
}

/* ---------------------------------------------------------------------------
 * Item 16i — the payment ledger
 * ------------------------------------------------------------------------ */

/**
 * `fleet.yaml#/components/schemas/SubscriptionPaymentRow.method` — **five values,
 * of which the platform can only produce four.**
 *
 * `subscription.yaml`'s `SubscriptionPayMethod` is
 * `[lankaqr_deeplink, lankaqr_scan, online_transfer, cash]`: **AL-59 removed
 * `onepay`** because "`payTo` is the fleet OWNER's verified account (AL-49) and
 * OnePay has one merchant account per merchant, so an OnePay subscription payment
 * would have landed subscriber money in MageRide's account". fleet.yaml's proxy
 * copy of the enum still lists it, and fleet-svc streams subscription-svc's body
 * back untouched — so the union is the wider of the two and `onepay` renders as a
 * historic method rather than as a blank cell on a row written before AL-59.
 *
 * Nothing on this portal *offers* a method: a subscription payment is initiated
 * by the passenger in their own app (SCR-PA-025a), and the only payment this
 * console writes is the owner's cash mark. So the drift costs a label and nothing
 * else. `test/subscriptions.test.ts` pins both enums and fails when they converge.
 */
export type SubscriptionPayMethod =
  | 'lankaqr_deeplink'
  | 'lankaqr_scan'
  | 'onepay'
  | 'online_transfer'
  | 'cash';

/** `SubscriptionPaymentRow.status` — `subscription.payments.status`'s CHECK (C005). */
export type SubscriptionPaymentStatus = 'initiated' | 'pending_verification' | 'paid' | 'failed';

/** `fleet.yaml#/components/schemas/SubscriptionPaymentRow`. */
export interface SubscriptionPaymentRow {
  readonly paymentId: string;
  readonly subscriptionId: string;
  readonly method: SubscriptionPayMethod;
  readonly amountMinor: number;
  readonly currency: string;
  readonly status: SubscriptionPaymentStatus;
  /** A `BusinessDate`, always the first of the month (`ck_payments_period_month_first`). */
  readonly periodMonth: string;
  /** The signed URL of the passenger's transfer screenshot, when there is one. */
  readonly slipUrl?: string;
  readonly paidAt?: string;
}

export type SubscriptionPaymentPage = CursorPage<SubscriptionPaymentRow>;

/**
 * Whether the owner's Confirm applies to this payment.
 *
 * `ConfirmAsync` passes `requiredStatus: pending_verification` to `MarkPaidAsync`
 * and refuses everything else with "Only a slip the passenger has uploaded can be
 * confirmed (US-23.4)". A `cash` row is the owner's own mark and is already paid;
 * an `initiated` one has no slip yet.
 */
export function canConfirm(payment: SubscriptionPaymentRow): boolean {
  return payment.status === 'pending_verification';
}

/** The month's live payment on a ledger, newest first — what a Confirm is about. */
export function pendingConfirmation(
  payments: readonly SubscriptionPaymentRow[],
): SubscriptionPaymentRow | null {
  return payments.find(canConfirm) ?? null;
}

/** Newest month first, which is the order an operator reads a ledger in. */
export function byNewestPeriod(a: SubscriptionPaymentRow, b: SubscriptionPaymentRow): number {
  return b.periodMonth.localeCompare(a.periodMonth);
}

/* ---------------------------------------------------------------------------
 * Money
 * ------------------------------------------------------------------------ */

/**
 * **Every rupee on these two screens belongs to the fleet owner.**
 *
 * AL-24: payments "route **to the fleet owner** (pass-through, not platform
 * revenue)"; BR-23.10 keeps them off MageRide's books; AL-49 makes the passenger's
 * `payTo` the owner's own verified bank account. The one thing MageRide charges
 * for is the monthly per-Mode-B-vehicle platform fee on SCR-FP-010, which is a
 * different screen about different money — so both of these screens carry the
 * sentence, and it is a constant rather than a habit so that removing it is a
 * diff somebody has to justify.
 */
export const SUBSCRIPTION_MONEY_IS_PASS_THROUGH = true;

/**
 * **An unpaid month is not a cash month until somebody pays it.**
 *
 * The wireframe's KPI is captioned "Cash due", but nothing on the platform knows
 * in advance how a subscriber will settle: a `cash` payment row is written by the
 * owner's own mark (`MarkCashAsync` inserts it), and a passenger who has not
 * chosen a rail yet has no row at all — which `ThisMonthStatusOf` reports as
 * `unpaid` exactly like one who opened the sheet and walked away. So the tile
 * counts what is **owed** and the caption says the Mark-received button is where a
 * cash payment becomes a fact.
 */
export const CASH_IS_NOT_KNOWN_IN_ADVANCE = true;

/**
 * **The roster carries no next-due date, so the cycle column names the cycle.**
 *
 * US-23.8 asks for "the cycle and next-due date … shown to both the subscriber and
 * the owner", and the wireframe writes "Joined 5 Jun · due 6 Jul". `SubscriberRow`
 * has neither field: `subscription.subscriptions` holds `next_due` and `join_day`
 * and `SubscriberRosterRow` reads both, but `SubscriberRowResponse.From` sends
 * neither, so the value exists one hop away and on no contract. The passenger's
 * own card (`GET /v1/mode-b/subscriptions/{passengerId}`) does carry `nextDue`,
 * and that route is not a fleet route.
 *
 * The column therefore names the cycle — which is the part that is on the wire —
 * and the screen says where the date is shown instead. Raised in the C116 handoff.
 */
export const NEXT_DUE_DATE_UNAVAILABLE = true;

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

export interface SubscriptionStatusView {
  readonly tone: StatusTone;
  readonly labelKey: FleetMessageKey;
}

/**
 * The roster's "This month" chip — the wireframe's Paid / verify-transfer /
 * cash-due, plus the muted row's own state.
 *
 * A muted row is answered first and always: an unsubscribed passenger's month is
 * not something the owner is chasing, and a red "Cash due" on a row somebody left
 * three weeks ago is a bill nobody owes.
 */
export function thisMonthView(row: SubscriberRow): SubscriptionStatusView {
  if (canDelete(row)) {
    return { tone: 'neutral', labelKey: 'fleet.subscriptions.status.unsubscribed' };
  }
  if (row.billing === 'free') {
    return { tone: 'neutral', labelKey: 'fleet.subscriptions.status.free' };
  }

  switch (row.thisMonthStatus) {
    case 'paid':
      return { tone: 'success', labelKey: 'fleet.subscriptions.status.paid' };
    case 'pending_verification':
      return { tone: 'warning', labelKey: 'fleet.subscriptions.status.verify' };
    default:
      return { tone: 'error', labelKey: 'fleet.subscriptions.status.due' };
  }
}

/** The ledger's status chip — `subscription.payments.status`'s own four. */
export function paymentStatusView(
  status: SubscriptionPaymentStatus,
): SubscriptionStatusView {
  switch (status) {
    case 'paid':
      return { tone: 'success', labelKey: 'fleet.payments.status.paid' };
    case 'pending_verification':
      return { tone: 'warning', labelKey: 'fleet.payments.status.verify' };
    case 'failed':
      return { tone: 'error', labelKey: 'fleet.payments.status.failed' };
    default:
      return { tone: 'info', labelKey: 'fleet.payments.status.initiated' };
  }
}

/** The rail a payment came in on, as one label. */
export function payMethodKey(method: SubscriptionPayMethod): FleetMessageKey {
  switch (method) {
    case 'lankaqr_deeplink':
      return 'fleet.payments.method.lankaqrDeeplink';
    case 'lankaqr_scan':
      return 'fleet.payments.method.lankaqrScan';
    case 'onepay':
      return 'fleet.payments.method.onepay';
    case 'online_transfer':
      return 'fleet.payments.method.onlineTransfer';
    default:
      return 'fleet.payments.method.cash';
  }
}

/** US-23.8's two cycles, as the sentence the column prints. */
export function cycleKey(cycle: SubscriptionCycle | undefined): FleetMessageKey {
  return cycle === 'join_anniversary'
    ? 'fleet.subscriptions.cycle.anniversary'
    : 'fleet.subscriptions.cycle.monthFirst';
}

/* ---------------------------------------------------------------------------
 * SCR-FP-012's summary KPIs
 * ------------------------------------------------------------------------ */

/**
 * The four tiles above the ledger, over **one roster read**.
 *
 * Each figure is the sum of the *fares* of the subscribers the roster puts in that
 * state, which is what one request can answer for a whole vehicle. The exact
 * amount of any individual payment is on the ledger below — a cash mark takes an
 * `amountMinor` of its own and need not equal the fare — and the screen says so
 * rather than presenting a derived figure as a reconciliation.
 *
 * A Free subscriber contributes to neither the money nor the counts of money:
 * BR-23.8 gives a Free service payment no fare at all, so counting one as
 * "unpaid" would invent a debt.
 */
export interface SubscriptionTotals {
  readonly paidMinor: number;
  readonly paidCount: number;
  readonly pendingMinor: number;
  readonly pendingCount: number;
  readonly dueMinor: number;
  readonly dueCount: number;
  readonly activeCount: number;
  readonly mutedCount: number;
  readonly freeCount: number;
}

export function subscriptionTotals(rows: readonly SubscriberRow[]): SubscriptionTotals {
  const totals = {
    paidMinor: 0,
    paidCount: 0,
    pendingMinor: 0,
    pendingCount: 0,
    dueMinor: 0,
    dueCount: 0,
    activeCount: 0,
    mutedCount: 0,
    freeCount: 0,
  };

  for (const row of rows) {
    if (canDelete(row)) {
      totals.mutedCount += 1;
      continue;
    }

    totals.activeCount += 1;

    if (row.billing !== 'paid') {
      totals.freeCount += 1;
      continue;
    }

    const fare = row.monthlyFareMinor ?? 0;
    switch (row.thisMonthStatus) {
      case 'paid':
        totals.paidMinor += fare;
        totals.paidCount += 1;
        break;
      case 'pending_verification':
        totals.pendingMinor += fare;
        totals.pendingCount += 1;
        break;
      default:
        totals.dueMinor += fare;
        totals.dueCount += 1;
        break;
    }
  }

  return totals;
}

/**
 * `mageride-subscriber-payments-vn-8810-01JQ….csv` — the export's own name.
 *
 * The plate and the grant id rather than the subscriber's name: a roster carries
 * Sinhala and Tamil names, and a file name is the one string on this platform that
 * has to survive a Windows download folder, an email attachment and a bank
 * reconciliation spreadsheet unchanged.
 */
export function paymentsFileName(vehiclePlate: string, subscriberId: string): string {
  const slug = vehiclePlate
    .replaceAll(/[^\w-]+/g, '-')
    .replaceAll(/-+/g, '-')
    .replaceAll(/^-|-$/g, '')
    .toLowerCase();
  return `mageride-subscriber-payments-${slug || 'vehicle'}-${subscriberId}.csv`;
}
