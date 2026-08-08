'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';

import { mutate } from '@/api/client';
import {
  isAdminId,
  isRefundKind,
  REFUNDS_PATH,
  reverseFeePath,
  type FeeReversal,
  type Refund,
} from '@/api/finance';
import { ProblemError } from '@/api/problem';
import { formatMoneyMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * SCR-AP-006's two money decisions: raising a refund (E-05) and reversing a
 * daily fee (US-14.11).
 *
 * ## Both are audited, and neither writes the row
 *
 * `REFUND_ISSUED` and `WALLET_FEE_REVERSED` are separate actions in
 * `AdminAuditActions.cs` and the reason is stated there: "a refund gives a
 * passenger back money they paid a gateway; a reversal puts credit onto a driver's
 * prepaid balance because MageRide charged them a fee it should not have.
 * Different payer, different rail, different URD §2.3 row — and an auditor asking
 * 'how much did we hand back to drivers last month' must not have to subtract one
 * from the other." Declaring them separately here is what keeps that true from the
 * console side.
 *
 * ## The reversal sends no `Idempotency-Key` of its own, on purpose
 *
 * Its ledger key is `adjustment:fee_reversal:{driverId}:{vehicleId}:{feeDate}` —
 * **composed from the business fact**, so a double click collides in the ledger and
 * comes back `replayed: true` with the original entry rather than crediting twice.
 * `mutate()` still sends a fresh header key, which is correct and does nothing: the
 * protection is one reversal per charge, ever, and it does not depend on this
 * process remembering anything.
 */

export interface RaiseRefundState {
  readonly message?: string;
  readonly field?: 'paymentId' | 'amount' | 'reasonCode';
}

export interface ReverseFeeState {
  readonly message?: string;
  readonly field?: 'driverId' | 'vehicleId' | 'feeDate' | 'amount' | 'reason';
  /**
   * What was posted, so the card can confirm it under the button that caused it.
   *
   * The two figures arrive **already formatted**, because the card is a client
   * component and this surface's rule is that every string reaches one translated
   * (`SignInForm`: importing the translator into the browser would ship all three
   * locale tables so one form could look up a handful of sentences). The action
   * runs on the server, where the translator and the operator's locale both are.
   */
  readonly posted?: {
    readonly driverId: string;
    readonly amount: string;
    readonly balanceAfter: string;
    /** The second press of a double click. The operator is told it did nothing. */
    readonly replayed: boolean;
  };
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * Rupees as typed → integer minor units, or `null` for anything that is not money.
 *
 * `Math.round` on the product is what stops `1.1 * 100` becoming `110.00000000000001`
 * and reaching a contract that says `type: integer`. An empty box is `undefined`
 * rather than zero — "the whole payment" and "nothing" are different instructions,
 * and the contract distinguishes them by the field's absence.
 */
function minorUnits(value: string): number | null | undefined {
  if (value === '') return undefined;

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) return null;

  return Math.round(parsed * 100);
}

/**
 * Raise a full, partial or overpaid reversal against a payment (E-05).
 *
 * **`amountMinor` is omitted on `full` and `overpaid_reversal`** and means the
 * whole payment; a `partial` must say how much. Sending a computed "whole amount"
 * instead would put this process's arithmetic between the operator and
 * `fares.ride_payments`, and the amount a refund may not exceed is the payment's,
 * which fare-svc holds and this screen only displays.
 */
export async function raiseRefund(
  _state: RaiseRefundState,
  formData: FormData,
): Promise<RaiseRefundState> {
  const t = await getTranslator();

  const paymentId = text(formData, 'paymentId');
  const kind = text(formData, 'kind');
  const reasonCode = text(formData, 'reasonCode');
  const amount = minorUnits(text(formData, 'amount'));

  if (!isAdminId(paymentId)) {
    return { message: t('admin.finance.refund.paymentRequired'), field: 'paymentId' };
  }
  if (!isRefundKind(kind)) return { message: t('admin.error.unexpected') };
  if (!reasonCode) {
    return { message: t('admin.finance.refund.reasonRequired'), field: 'reasonCode' };
  }
  if (amount === null) {
    return { message: t('admin.finance.refund.amountInvalid'), field: 'amount' };
  }
  if (kind === 'partial' && (amount === undefined || amount < 1)) {
    return { message: t('admin.finance.refund.amountRequired'), field: 'amount' };
  }

  let refund: Refund;

  try {
    ({ data: refund } = await mutate<
      Refund,
      { paymentId: string; kind: string; amountMinor?: number; reasonCode: string }
    >({
      method: 'POST',
      path: REFUNDS_PATH,
      body: {
        paymentId,
        kind,
        // Only a partial carries one. On the other two the field's absence is the
        // instruction, and admin-bff reads it that way.
        ...(kind === 'partial' && amount !== undefined ? { amountMinor: amount } : {}),
        reasonCode,
      },
      audit: { action: 'REFUND_ISSUED', entity: 'ride_payment', entityId: paymentId },
    }));
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  // A raised refund joins the queue as a `refund` row and the `overpaid` row it
  // came from is gone, so there is nothing on this page to stay on. The status
  // travels so the queue can say which of the four the platform recorded.
  redirect(`/finance/refunds?raised=${refund.status}`);
}

/**
 * Reverse a daily-fee deduction onto a driver's wallet (US-14.11).
 *
 * **Gated on `Driver wallet adjustments · Write`**, whose URD §2.3 cells are ✅ for
 * exactly Super Admin and Finance — so "reversals are Finance/Super-Admin only" is
 * not a role list written here or in admin-bff, it is what the matrix already says.
 * This process checks nothing about roles and must not start.
 *
 * **A day whose fee was waived under D-13's first-trip rule moved no money** and is
 * a `409` from admin-bff. That refusal is not duplicated here: it is a fact about
 * `billing.daily_fee_charges` that this screen cannot see, and guessing at it would
 * be a second rule that could disagree with the first.
 */
export async function reverseFee(
  _state: ReverseFeeState,
  formData: FormData,
): Promise<ReverseFeeState> {
  const t = await getTranslator();

  const driverId = text(formData, 'driverId');
  const vehicleId = text(formData, 'vehicleId');
  const feeDate = text(formData, 'feeDate');
  const reason = text(formData, 'reason');
  const amount = minorUnits(text(formData, 'amount'));

  if (!isAdminId(driverId)) {
    return { message: t('admin.finance.reversal.driverRequired'), field: 'driverId' };
  }
  if (!isAdminId(vehicleId)) {
    return { message: t('admin.finance.reversal.vehicleRequired'), field: 'vehicleId' };
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(feeDate)) {
    return { message: t('admin.finance.reversal.dateRequired'), field: 'feeDate' };
  }
  if (amount === null) {
    return { message: t('admin.finance.refund.amountInvalid'), field: 'amount' };
  }
  if (!reason) {
    return { message: t('admin.finance.reversal.reasonRequired'), field: 'reason' };
  }

  let posted: FeeReversal;

  try {
    ({ data: posted } = await mutate<
      FeeReversal,
      { feeDate: string; vehicleId: string; amountMinor?: number; reason: string }
    >({
      method: 'POST',
      path: reverseFeePath(driverId),
      body: {
        feeDate,
        vehicleId,
        // Absent means the full charged amount, which is the common case and the
        // one an operator should not have to look up to get right.
        ...(amount === undefined ? {} : { amountMinor: amount }),
        reason,
      },
      audit: { action: 'WALLET_FEE_REVERSED', entity: 'driver_wallet', entityId: driverId },
    }));
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  // The ledger moved, so the transactions report beside this card is now stale.
  revalidatePath('/finance/adjustments');
  revalidatePath('/finance/transactions');

  const locale = await getLocale();
  const money = (minor: number) =>
    t('admin.dashboard.money', { amount: formatMoneyMinor(locale, minor) });

  return {
    posted: {
      driverId,
      amount: money(posted.amountMinor),
      balanceAfter: money(posted.balanceAfterMinor),
      replayed: posted.replayed,
    },
  };
}
