'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  acceptRequestTarget,
  confirmPaymentTarget,
  rejectRequestTarget,
  subscriberFareTarget,
  subscriberMarkCashTarget,
  subscriberTarget,
  REJECT_REASON_MAX_LENGTH,
  type AcceptRequestResult,
  type MarkCashBody,
  type RejectRequestBody,
  type SetFareBody,
  type SubscriberRow,
  type SubscriptionPaymentRow,
} from '@/api/subscriptions';
// The single rupees → minor-units conversion on this portal (C113's rule). A
// second one here would be a second place money changes shape.
import { fareMinorFrom } from '@/api/vehicles';
import { formatFareMinor, formatMonth } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';

import { canManageSubscribers } from './access';
import { getSession } from './session';

/**
 * **SCR-FP-011 and SCR-FP-012's six writes** — Epic 23's owner side, as server
 * actions.
 *
 * | action | route | seat fleet-svc declares |
 * |---|---|---|
 * | {@link acceptRequest} | `POST …/requests/{id}/accept` | Manager |
 * | {@link rejectRequest} | `POST …/requests/{id}/reject` | Manager |
 * | {@link setSubscriberFare} | `PUT …/subscribers/{id}/fare` | Owner |
 * | {@link markCashReceived} | `POST …/subscribers/{id}/mark-cash` | Owner |
 * | {@link confirmTransfer} | `POST …/payments/{id}/confirm` | Owner |
 * | {@link deleteSubscriber} | `DELETE …/subscribers/{id}` | Owner |
 *
 * ## The row is `fleet-operations`; the Owner half is the seat, checked here
 *
 * Every one of the six declares `{ area: 'fleet-operations', requiresApprovedOrg:
 * true }` — the whole proxy group is built on `FleetVehiclesGroup` and inherits
 * its `RequireApprovedFleet()`, and `write` on `fleet-operations` is exactly what
 * a Manager holds and a Viewer does not. The four that fleet-svc puts behind
 * `RequireFleetSubRole(Owner)` check `canManageSubscribers()` **as well**, because
 * URD §2.3 has no row that separates an Owner from a Manager on `fleet-operations`
 * — the same gap `canManageTeam()` exists for, and the same answer.
 *
 * Reaching for the `fleet-billing` row instead would have answered "Owner only"
 * without a second check, and would have been wrong about what the money is:
 * that row is MageRide's monthly invoice to this organisation, and a subscriber's
 * fare never touches it (AL-24 — "pass-through, not platform revenue").
 *
 * ## An accept is not idempotent by construction, so the key is
 *
 * `POST …/accept` writes a grant **and** starts a subscription in one
 * transaction, and the proxy "must not invent retries its caller did not ask for
 * — a retried accept is a second grant". `mutate()` sends an `Idempotency-Key` on
 * every write and subscription-svc's kernel requires one; a double-clicked Accept
 * therefore replays the first response rather than granting twice.
 *
 * ## Every sentence is composed here
 *
 * A client component cannot be handed a translator or a function to build one
 * with (React refuses to serialise a function across the boundary — C115's rule,
 * asserted by `test/fences.test.ts`), so a confirmation that names an amount or a
 * month is written in the action, where the locale and the Colombo formatter
 * already are.
 */

/** `fleet-operations` is URD §2.3's "Fleet — org & vehicle onboarding, driver assignment, scheduling". */
const REQUIRES = { area: 'fleet-operations', requiresApprovedOrg: true } as const;

export interface RequestActionState {
  readonly message?: string;
  /** The confirmation, already written — "Sunethra now has access to VN-8810." */
  readonly done?: string;
}

export interface SubscriberActionState {
  readonly message?: string;
  readonly field?: 'fare' | 'amount';
  readonly done?: string;
}

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/**
 * The refusal a Manager gets if an Owner-only control is ever drawn for them.
 *
 * `null` when the caller may act. It is checked before the request rather than
 * after the 403, for the reason SCR-FP-010 reads nothing until the caller is
 * entitled: a refusal the portal already knows about should not become an access
 * attempt in somebody's audit trail.
 */
async function ownerRefusal(): Promise<string | null> {
  const [session, t] = await Promise.all([getSession(), getTranslator()]);
  if (session && canManageSubscribers(session)) return null;
  return t('fleet.subscriptions.ownerOnly');
}

/** Both screens show the same rows, so both are revalidated by every write. */
function revalidateSubscriberScreens(): void {
  revalidatePath('/subscriptions');
  revalidatePath('/payments');
}

/* ---------------------------------------------------------------------------
 * Item 15 — the request queue
 * ------------------------------------------------------------------------ */

/**
 * `POST …/vehicles/{vehicleId}/requests/{requestId}/accept` — US-23.1.
 *
 * "Accepting grants tracking access and starts the subscription", and the two are
 * one transaction on the far side: the response carries the `grantId` and the
 * `subscriptionId` it created. The cycle it starts on is the vehicle's — 1st of
 * the month or the anniversary of today (US-23.8) — and is chosen by
 * subscription-svc, not here.
 */
export async function acceptRequest(
  _state: RequestActionState,
  formData: FormData,
): Promise<RequestActionState> {
  const t = await getTranslator();

  const vehicleId = text(formData, 'vehicleId');
  const requestId = text(formData, 'requestId');
  if (!vehicleId || !requestId) return { message: t('fleet.subscriptions.error.requestMissing') };

  const passenger = text(formData, 'passenger');

  try {
    await mutate<AcceptRequestResult>({
      method: 'POST',
      org: acceptRequestTarget(vehicleId, requestId),
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'conflict') return { message: t('fleet.subscriptions.error.requestDecided') };
    return { message: t(error.messageKey) };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.request.accepted', {
      passenger: passenger || t('fleet.subscriptions.passengerUnnamed'),
    }),
  };
}

/**
 * `POST …/vehicles/{vehicleId}/requests/{requestId}/reject` — terminal.
 *
 * "No grant and no subscription are created", and the passenger has to request
 * again to rejoin (AL-25's same rule, from the other end). The optional `reason`
 * is capped at 500 by the contract and is trimmed to it here rather than sent to
 * be refused — a rejection that failed validation would leave the request in the
 * queue with the operator believing it gone.
 */
export async function rejectRequest(
  _state: RequestActionState,
  formData: FormData,
): Promise<RequestActionState> {
  const t = await getTranslator();

  const vehicleId = text(formData, 'vehicleId');
  const requestId = text(formData, 'requestId');
  if (!vehicleId || !requestId) return { message: t('fleet.subscriptions.error.requestMissing') };

  const passenger = text(formData, 'passenger');
  const reason = text(formData, 'reason').slice(0, REJECT_REASON_MAX_LENGTH);

  try {
    await mutate<unknown, RejectRequestBody>({
      method: 'POST',
      org: rejectRequestTarget(vehicleId, requestId),
      body: reason ? { reason } : {},
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'conflict') return { message: t('fleet.subscriptions.error.requestDecided') };
    return { message: t(error.messageKey) };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.request.rejected', {
      passenger: passenger || t('fleet.subscriptions.passengerUnnamed'),
    }),
  };
}

/* ---------------------------------------------------------------------------
 * Item 16f — the fare, the cash mark and the slip
 * ------------------------------------------------------------------------ */

/**
 * `PUT …/subscribers/{subscriberId}/fare` — US-23.7, "each subscriber may pay a
 * different amount".
 *
 * The override is per subscriber and replaces the vehicle's default for them
 * alone; the vehicle's own default is SCR-FP-004's control and is not touched
 * from here. A Free subscription has no fare to set — `ck_subscriptions_fare`
 * refuses one and the service answers `409 conflict` — so the control is not
 * drawn for one (`canSetFare`), and the conflict copy names the reason for the
 * case where the roster moved under the operator.
 */
export async function setSubscriberFare(
  _state: SubscriberActionState,
  formData: FormData,
): Promise<SubscriberActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const refusal = await ownerRefusal();
  if (refusal) return { message: refusal };

  const vehicleId = text(formData, 'vehicleId');
  const subscriberId = text(formData, 'subscriberId');
  if (!vehicleId || !subscriberId) {
    return { message: t('fleet.subscriptions.error.subscriberMissing') };
  }

  const monthlyFareMinor = fareMinorFrom(text(formData, 'fare'));
  if (monthlyFareMinor === null) {
    return { message: t('fleet.subscriptions.error.fareInvalid'), field: 'fare' };
  }

  try {
    await mutate<SubscriberRow, SetFareBody>({
      method: 'PUT',
      org: subscriberFareTarget(vehicleId, subscriberId),
      body: { monthlyFareMinor },
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'conflict') {
      return { message: t('fleet.subscriptions.error.fareNotCollectable') };
    }
    return {
      message: t(error.messageKey),
      ...(error.code === 'invalid-amount' ? { field: 'fare' as const } : {}),
    };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.fare.saved', {
      amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, monthlyFareMinor) }),
    }),
  };
}

/**
 * `POST …/subscribers/{subscriberId}/mark-cash` — US-23.6.
 *
 * "As a passenger paying cash, I hand it to whoever collects for the fleet; **only
 * the fleet Owner can mark it received** in the web portal. Once marked received,
 * the passenger's subscription card shows **Paid**." So this write is the whole of
 * the cash rail: there is no gateway leg, and the row it creates is the record
 * that money changed hands.
 *
 * `periodMonth` is deliberately **not** sent. `MarkCashAsync` defaults it to
 * `DuePeriodOf(subscription, now)` — the month the subscription is actually due —
 * and a portal that computed a month from the browser's clock would settle the
 * wrong one either side of a Colombo month boundary. The amount is sent because
 * the contract requires it and because a cash payment need not equal the fare;
 * the form offers the fare as its default.
 */
export async function markCashReceived(
  _state: SubscriberActionState,
  formData: FormData,
): Promise<SubscriberActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const refusal = await ownerRefusal();
  if (refusal) return { message: refusal };

  const vehicleId = text(formData, 'vehicleId');
  const subscriberId = text(formData, 'subscriberId');
  if (!vehicleId || !subscriberId) {
    return { message: t('fleet.subscriptions.error.subscriberMissing') };
  }

  const amountMinor = fareMinorFrom(text(formData, 'amount'));
  if (amountMinor === null || amountMinor <= 0) {
    return { message: t('fleet.subscriptions.error.amountInvalid'), field: 'amount' };
  }

  let payment: SubscriptionPaymentRow;
  try {
    const outcome = await mutate<SubscriptionPaymentRow, MarkCashBody>({
      method: 'POST',
      org: subscriberMarkCashTarget(vehicleId, subscriberId),
      body: { amountMinor },
      requires: REQUIRES,
    });
    payment = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // Three different `409`s reach here — already paid, no live subscription, and
    // a Free service payment — and the service's own English detail cannot be
    // shown (D3' §0: `title` is "never localised"). One sentence covers all
    // three honestly: the month is not open for a cash payment.
    if (error.code === 'conflict') return { message: t('fleet.subscriptions.error.cashNotDue') };
    return {
      message: t(error.messageKey),
      ...(error.code === 'invalid-amount' ? { field: 'amount' as const } : {}),
    };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.cash.received', {
      amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, payment.amountMinor) }),
      month: formatMonth(locale, payment.periodMonth) ?? payment.periodMonth,
    }),
  };
}

/**
 * `POST /v1/fleets/{own id}/payments/{paymentId}/confirm` — US-23.4/16f.
 *
 * The passenger uploaded a screenshot of a bank transfer and the payment has been
 * `pending_verification` since; confirming it is the owner saying the money
 * arrived in their account. `ConfirmAsync` passes
 * `requiredStatus: pending_verification` and refuses anything else — "only a slip
 * the passenger has uploaded can be confirmed" — so the control is drawn for that
 * status alone (`canConfirm`).
 *
 * There is no reject verb, and that is the platform's shape rather than an
 * omission: a slip that is not good is left unconfirmed and the month stays open.
 */
export async function confirmTransfer(
  _state: SubscriberActionState,
  formData: FormData,
): Promise<SubscriberActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const refusal = await ownerRefusal();
  if (refusal) return { message: refusal };

  const paymentId = text(formData, 'paymentId');
  if (!paymentId) return { message: t('fleet.subscriptions.error.paymentMissing') };

  let payment: SubscriptionPaymentRow;
  try {
    const outcome = await mutate<SubscriptionPaymentRow>({
      method: 'POST',
      org: confirmPaymentTarget(paymentId),
      requires: REQUIRES,
    });
    payment = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'conflict') {
      return { message: t('fleet.subscriptions.error.slipNotPending') };
    }
    return { message: t(error.messageKey) };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.slip.confirmed', {
      amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, payment.amountMinor) }),
      month: formatMonth(locale, payment.periodMonth) ?? payment.periodMonth,
    }),
  };
}

/* ---------------------------------------------------------------------------
 * Item 17 — the hard delete
 * ------------------------------------------------------------------------ */

/**
 * `DELETE …/subscribers/{subscriberId}` — AL-25 and US-23.12.
 *
 * "An unsubscribed passenger remains visible but **muted** in the Fleet Portal
 * until the owner deletes that subscriber from the corresponding vehicle." The
 * order is the passenger's first and cannot be reversed from here:
 * `DeleteSubscriberAsync` answers `409 conflict` for a row that is still active,
 * because "only a passenger can end their own subscription". So Delete is drawn on
 * a muted row and nowhere else, and the conflict copy says whose act is missing.
 *
 * It is a hard delete of the grant. The passenger loses visibility of the vehicle
 * and has to request again to rejoin — which is the same sentence the wireframe
 * puts under the table.
 */
export async function deleteSubscriber(
  _state: SubscriberActionState,
  formData: FormData,
): Promise<SubscriberActionState> {
  const t = await getTranslator();

  const refusal = await ownerRefusal();
  if (refusal) return { message: refusal };

  const vehicleId = text(formData, 'vehicleId');
  const subscriberId = text(formData, 'subscriberId');
  if (!vehicleId || !subscriberId) {
    return { message: t('fleet.subscriptions.error.subscriberMissing') };
  }

  const passenger = text(formData, 'passenger');

  try {
    await mutate({
      method: 'DELETE',
      org: subscriberTarget(vehicleId, subscriberId),
      requires: REQUIRES,
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    if (error.code === 'conflict') {
      return { message: t('fleet.subscriptions.error.stillSubscribed') };
    }
    return { message: t(error.messageKey) };
  }

  revalidateSubscriberScreens();

  return {
    done: t('fleet.subscriptions.subscriber.deleted', {
      passenger: passenger || t('fleet.subscriptions.passengerUnnamed'),
    }),
  };
}
