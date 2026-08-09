import type { StatusTone } from '@mageride/ui';

import {
  canDelete,
  canMarkCash,
  canSetFare,
  cycleKey,
  payMethodKey,
  paymentStatusView,
  thisMonthView,
  type AccessRequest,
  type SubscriberRow,
  type SubscriptionPaymentRow,
} from '@/api/subscriptions';
import type { FleetVehicle } from '@/api/vehicles';
import { formatDay, formatFareMinor, formatInstant, formatMonth } from '@/i18n/format';
import type { FleetTranslator, Locale } from '@/i18n';

/**
 * The three tables of SCR-FP-011 and SCR-FP-012, as rows of already-formatted
 * strings.
 *
 * Every date, every amount and every sentence is composed **here**, on the
 * server, because the tables' action controls are client components and React
 * refuses to serialise a translator across that boundary (C115's rule, asserted
 * by `test/fences.test.ts`). What crosses is a row of strings and a handful of
 * booleans the row already decided.
 *
 * The booleans are the platform's own preconditions, read off the wire row —
 * `canSetFare`, `canMarkCash`, `canDelete` and `canConfirm` in `@/api/subscriptions`
 * — never re-derived here. A control that the service would refuse is not drawn;
 * a control that is drawn is one whose refusal would be news.
 */

/* ---------------------------------------------------------------------------
 * Item 15 — the request queue
 * ------------------------------------------------------------------------ */

export interface RequestRowView {
  readonly key: string;
  readonly requestId: string;
  readonly passenger: string;
  /** `PhoneMasked`, as subscription-svc masked it. Never unmasked here. */
  readonly mobile: string | null;
  readonly passengerId: string;
  readonly requested: string;
}

export function requestRows(
  requests: readonly AccessRequest[],
  locale: Locale,
  t: FleetTranslator,
): RequestRowView[] {
  return requests.map((request) => ({
    key: request.requestId,
    requestId: request.requestId,
    passenger: request.passengerName?.trim() || t('fleet.subscriptions.passengerUnnamed'),
    mobile: request.passengerMobileMasked ?? null,
    passengerId: request.passengerId,
    requested: formatDay(locale, request.createdAt) ?? request.createdAt,
  }));
}

/* ---------------------------------------------------------------------------
 * Items 16 and 17 — the roster
 * ------------------------------------------------------------------------ */

/** One row's awaiting-verification payment, as the roster's Confirm needs it. */
export interface PendingSlip {
  readonly paymentId: string;
  readonly slipUrl: string | null;
}

export interface SubscriberRowView {
  readonly key: string;
  readonly subscriberId: string;
  readonly passenger: string;
  readonly mobile: string | null;
  readonly passengerId: string;
  /** "Rs 6,000/mo", or `null` on a Free subscription — BR-23.8 gives it no fare. */
  readonly fare: string | null;
  /** The same amount as rupees, for the edit field's own default. */
  readonly fareInput: string;
  readonly cycle: string;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  readonly muted: boolean;
  readonly maySetFare: boolean;
  readonly mayMarkCash: boolean;
  readonly mayDelete: boolean;
  /**
   * The `pending_verification` payment this month's chip is about, when the
   * caller could read the ledger to find one — see the page's own note on why
   * that read happens for an Owner and for pending rows only.
   */
  readonly pendingPaymentId: string | null;
  /**
   * The passenger's slip, so the owner can look at what they are confirming
   * before they confirm it. `null` when the payment carries none — the row is
   * `pending_verification` from the moment the slip is attached, so in practice
   * one exists, and a Confirm with nothing to look at is still the right button.
   */
  readonly pendingSlipUrl: string | null;
  /** `/payments?vehicle=…&subscriber=…` — the wireframe's per-row **Payments**. */
  readonly paymentsHref: string;
}

export function subscriberRows({
  rows,
  vehicleId,
  pendingPayments,
  locale,
  t,
}: {
  readonly rows: readonly SubscriberRow[];
  readonly vehicleId: string;
  /** The awaiting-verification payment of each row whose month has one. */
  readonly pendingPayments: ReadonlyMap<string, PendingSlip>;
  readonly locale: Locale;
  readonly t: FleetTranslator;
}): SubscriberRowView[] {
  return rows.map((row) => {
    const view = thisMonthView(row);
    const fareMinor = row.monthlyFareMinor ?? null;
    const slip =
      row.thisMonthStatus === 'pending_verification'
        ? (pendingPayments.get(row.subscriberId) ?? null)
        : null;

    return {
      key: row.subscriberId,
      subscriberId: row.subscriberId,
      passenger: row.name?.trim() || t('fleet.subscriptions.passengerUnnamed'),
      mobile: row.mobileMasked ?? null,
      passengerId: row.passengerId,
      fare:
        row.billing === 'paid' && fareMinor !== null
          ? t('fleet.subscriptions.farePerMonth', {
              amount: formatFareMinor(locale, fareMinor),
            })
          : null,
      fareInput: fareMinor === null ? '' : String(fareMinor / 100),
      cycle: t(cycleKey(row.cycle)),
      statusLabel: t(view.labelKey),
      statusTone: view.tone,
      muted: canDelete(row),
      maySetFare: canSetFare(row),
      mayMarkCash: canMarkCash(row),
      mayDelete: canDelete(row),
      pendingPaymentId: slip?.paymentId ?? null,
      pendingSlipUrl: slip?.slipUrl ?? null,
      paymentsHref: `/payments?vehicle=${encodeURIComponent(vehicleId)}&subscriber=${encodeURIComponent(row.subscriberId)}`,
    };
  });
}

/* ---------------------------------------------------------------------------
 * Item 16i — the ledger
 * ------------------------------------------------------------------------ */

export interface PaymentRowView {
  readonly key: string;
  readonly paymentId: string;
  readonly month: string;
  /** When it was paid, or a dash — an unsettled row has no date yet. */
  readonly when: string | null;
  readonly method: string;
  readonly amount: string;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  readonly mayConfirm: boolean;
  /** The signed URL of the passenger's screenshot, served by subscription-svc. */
  readonly slipUrl: string | null;
}

export function paymentRows(
  payments: readonly SubscriptionPaymentRow[],
  locale: Locale,
  t: FleetTranslator,
): PaymentRowView[] {
  return payments.map((payment) => ({
    key: payment.paymentId,
    paymentId: payment.paymentId,
    month: formatMonth(locale, payment.periodMonth) ?? payment.periodMonth,
    when: formatInstant(locale, payment.paidAt),
    method: t(payMethodKey(payment.method)),
    amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, payment.amountMinor) }),
    statusLabel: t(paymentStatusView(payment.status).labelKey),
    statusTone: paymentStatusView(payment.status).tone,
    mayConfirm: payment.status === 'pending_verification',
    slipUrl: payment.slipUrl ?? null,
  }));
}

/* ---------------------------------------------------------------------------
 * The vehicle scope — AL-23 as a control
 * ------------------------------------------------------------------------ */

export interface VehicleChoice {
  readonly vehicleId: string;
  /** "VN-8810 · Paid" — the picker's option and the tables' headings. */
  readonly label: string;
  /** The plate on its own, for a caption that is already about this vehicle. */
  readonly plate: string;
  /** Whether this vehicle collects a fare at all (AL-51's Service payment). */
  readonly paid: boolean;
  /**
   * The vehicle's own default monthly fare — the wireframe's "Paid · default
   * Rs 6,000/mo". It is what a new subscription starts on; US-23.7's per-subscriber
   * override then replaces it for that subscriber alone, which is why the roster
   * can show four different amounts under this one heading. Setting it is
   * SCR-FP-004's control, not this screen's.
   */
  readonly defaultFareMinor: number | null;
}

/**
 * The vehicles this screen can be scoped to: **Mode B only**.
 *
 * A Mode A vehicle has no subscribers by construction — `subscription.grants` are
 * written by an Epic 23 access request and Mode A is scheduled public transport —
 * so offering one would be offering a page that is empty for a reason the operator
 * has to work out. Both Free and Paid Mode B vehicles are offered: a Free one
 * still has a request queue and a roster, and only the fare and payment columns
 * fall away (the wireframe's own states line).
 */
export function subscribableVehicles(
  vehicles: readonly FleetVehicle[],
  t: FleetTranslator,
): VehicleChoice[] {
  return vehicles
    .filter((vehicle) => vehicle.mode === 'B')
    .map((vehicle) => ({
      vehicleId: vehicle.vehicleId,
      plate: vehicle.registrationNumber,
      label: t('fleet.subscriptions.vehicleOption', {
        plate: vehicle.registrationNumber,
        service: t(
          vehicle.modeBBilling === 'paid'
            ? 'fleet.vehicles.servicePayment.paid'
            : 'fleet.vehicles.servicePayment.free',
        ),
      }),
      paid: vehicle.modeBBilling === 'paid',
      defaultFareMinor: vehicle.defaultMonthlyFareMinor ?? null,
    }));
}

/**
 * Which vehicle the screen is showing: the one in the URL if it is one of ours,
 * and the first Mode B vehicle otherwise.
 *
 * An id from another organisation resolves to `null` rather than to a request —
 * the roster read would be refused by fleet-svc's own scope check anyway
 * ("This vehicle is not in the organisation's fleet"), and answering here means
 * a pasted id produces a sentence instead of a problem panel.
 */
export function selectedVehicle(
  choices: readonly VehicleChoice[],
  wanted: string | undefined,
): VehicleChoice | null {
  if (wanted) return choices.find((choice) => choice.vehicleId === wanted) ?? null;
  return choices[0] ?? null;
}

/* ---------------------------------------------------------------------------
 * SCR-FP-012's CSV
 * ------------------------------------------------------------------------ */

/**
 * The ledger as rows for `csvRows`, with **no figure that is not on the screen**.
 *
 * `web_fleet.html` draws "Export CSV" on SCR-FP-012 and no contract carries a
 * subscription-payment export route — subscription-svc's only document route is
 * the signed slip/QR file — so the file is written in this repo, exactly as
 * SCR-FP-009's analytics CSV is and for the same reason. SCR-FP-010's invoice
 * export is the opposite case: fleet-billing-svc renders that one, so the portal
 * streams it.
 *
 * The amount is printed **twice** — grouped rupees for a person, integer minor
 * units for a reconciliation against this platform — which is the convention
 * fleet-billing-svc's own CSV set.
 */
export function paymentsCsv(
  payments: readonly SubscriptionPaymentRow[],
  context: { readonly vehicle: string; readonly subscriber: string },
  locale: Locale,
  t: FleetTranslator,
): (string | number)[][] {
  const header = [
    t('fleet.payments.csv.vehicle'),
    t('fleet.payments.csv.subscriber'),
    t('fleet.payments.csv.month'),
    t('fleet.payments.csv.paidAt'),
    t('fleet.payments.csv.method'),
    t('fleet.payments.csv.amount'),
    t('fleet.payments.csv.amountMinor'),
    t('fleet.payments.csv.currency'),
    t('fleet.payments.csv.status'),
    t('fleet.payments.csv.paymentId'),
  ];

  const rows = payments.map((payment) => [
    context.vehicle,
    context.subscriber,
    payment.periodMonth,
    payment.paidAt ?? '',
    t(payMethodKey(payment.method)),
    formatFareMinor(locale, payment.amountMinor),
    payment.amountMinor,
    payment.currency,
    t(paymentStatusView(payment.status).labelKey),
    payment.paymentId,
  ]);

  return [header, ...rows];
}
