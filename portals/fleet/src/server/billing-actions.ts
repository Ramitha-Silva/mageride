'use server';

import { revalidatePath } from 'next/cache';

import {
  invoicePayTarget,
  isTopupMethod,
  topupTarget,
  FLEET_WALLET_TOPUP,
  MAX_TOPUP_MINOR,
  MIN_TOPUP_MINOR,
  TOPUP_PENDING_WINDOW_SECONDS,
  type FleetInvoice,
  type FleetTopup,
  type TopupMethod,
} from '@/api/billing';
import { mutate, read } from '@/api/client';
import { ProblemError } from '@/api/problem';
// The single rupees → minor-units conversion on this portal (C113's rule). A
// second one here would be a second place money changes shape.
import { fareMinorFrom } from '@/api/vehicles';
import { formatFareMinor } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';

/**
 * **SCR-FP-010's two writes** — top up the fleet wallet, and settle an invoice
 * from it (US-13.10, US-13.10b).
 *
 * ## Both are the Owner's, and the declaration says so on both halves
 *
 * `FleetBillingAccessFilter` gates every route in `fleet-billing.yaml` — reads
 * included — on the Owner sub-role **and** an APPROVED organisation, which is
 * `canReadBilling()` on this side. The mutations declare
 * `{ area: 'fleet-billing', requiresApprovedOrg: true }`, and `write` on
 * `fleet-billing` is exactly what URD §2.1 leaves the Owner and takes from the
 * Manager — so `canMutate` answers the seat without this file naming it, the same
 * way `schedule-actions` does for `fleet-operations`.
 *
 * ## Nothing here credits anything
 *
 * `POST …/wallet/topup` **opens a payment session**. The wallet is credited on the
 * provider's callback with a balanced double-entry journal credit (D-09), "never
 * here: a session the gateway accepted has moved no money, and treating it as a
 * credit is how a balance grows by abandoning a payment page". So the action
 * answers a session for the operator to complete, and the screen's balance moves
 * when — and only when — the money arrives.
 *
 * ## No `returnUrl` is sent, and that is deliberate
 *
 * The field is optional and is passed straight to OnePay as the page to come back
 * to. This portal has no configured public origin — every URL it knows is
 * `MAGERIDE_API_BASE_URL` and the two OIDC redirect URIs, each set explicitly —
 * and deriving one from the request's own `Host` would hand a payment gateway a
 * value the caller controls. The operator comes back to this screen themselves and
 * presses **Check payment**, which is what the 90-second poll is for.
 */

export interface TopupActionState {
  readonly message?: string;
  readonly field?: 'amount' | 'method';
  /** The session, already written for the screen. */
  readonly topup?: TopupView;
}

/**
 * A top-up session as the form renders it.
 *
 * Every string is composed **here**, on the server, where the translator and the
 * Colombo formatter are. A client component cannot be handed either — React
 * refuses to serialise a function across the boundary — and a payment screen that
 * built its own sentences would be a second place this portal decides what a
 * `Pending` session means.
 */
export interface TopupView {
  readonly topupId: string;
  readonly state: FleetTopup['state'];
  readonly headline: string;
  readonly status: string;
  readonly tone: 'info' | 'success' | 'warning' | 'error';
  /** OnePay's hosted page, or AL-15's deep link into the bank app. */
  readonly continueUrl: string | null;
  readonly continueLabel: string | null;
  /** The EMVCo fallback, where the deployment's acquirer has given it a template. */
  readonly qrPayload: string | null;
}

export interface PayActionState {
  readonly message?: string;
  /** The confirmation, already written. */
  readonly paid?: string;
}

/** Both routes are `fleet-billing`, and the whole group is approval-gated. */
const REQUIRES = { area: 'fleet-billing', requiresApprovedOrg: true } as const;

function text(formData: FormData, name: string): string {
  return String(formData.get(name) ?? '').trim();
}

/* ---------------------------------------------------------------------------
 * Top up
 * ------------------------------------------------------------------------ */

interface TopupBody {
  readonly amountMinor: number;
  readonly method: TopupMethod;
}

/** `POST /v1/fleets/{id}/wallet/topup` — US-13.10b. */
export async function topUpWallet(
  _state: TopupActionState,
  formData: FormData,
): Promise<TopupActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const method = text(formData, 'method');
  if (!isTopupMethod(method)) {
    // Not reachable from the form's own radios. Sent as a field error rather than
    // as a request, because AL-05's refusal belongs to the platform and this is
    // simply a value that was never one of the two.
    return { message: t('fleet.billing.error.methodInvalid'), field: 'method' };
  }

  const amountMinor = fareMinorFrom(text(formData, 'amount'));
  if (amountMinor === null || amountMinor <= 0) {
    return { message: t('fleet.billing.error.amountInvalid'), field: 'amount' };
  }
  if (amountMinor < MIN_TOPUP_MINOR || amountMinor > MAX_TOPUP_MINOR) {
    return {
      message: t('fleet.billing.error.amountRange', {
        min: t('fleet.money.rupees', { amount: formatFareMinor(locale, MIN_TOPUP_MINOR) }),
        max: t('fleet.money.rupees', { amount: formatFareMinor(locale, MAX_TOPUP_MINOR) }),
      }),
      field: 'amount',
    };
  }

  let topup: FleetTopup;
  try {
    const outcome = await mutate<FleetTopup, TopupBody>({
      method: 'POST',
      org: FLEET_WALLET_TOPUP,
      body: { amountMinor, method },
      requires: REQUIRES,
    });
    topup = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;

    // `dependency-unavailable` is the rail this deployment has not configured —
    // "AL-05 leaves LankaQR as the other rail; there is no bank-transfer
    // fallback" — so the operator is told which one to use instead.
    if (error.code === 'dependency-unavailable' || error.code === 'gateway-error') {
      return { message: t('fleet.error.railUnavailable'), field: 'method' };
    }
    return {
      message: t(error.messageKey),
      ...(error.code === 'invalid-amount' ? { field: 'amount' as const } : {}),
    };
  }

  return { topup: topupView(topup, locale, t) };
}

/**
 * `GET …/wallet/topup/{topupId}` — the poll D6' §7.1's window has a client run.
 *
 * A **button**, not a timer. The credit arrives on the provider's callback while
 * the operator is on a bank app or a hosted card page — often on another device —
 * so a page polling every two seconds would be this console asking a question
 * nobody is waiting on the answer to. Pressing it after paying is one request.
 *
 * `revalidatePath` on success, because the balance and the invoice's payable
 * state have both moved.
 */
export async function checkTopup(topupId: string): Promise<TopupActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  try {
    const topup = await read<FleetTopup>({ org: topupTarget(topupId) });
    if (topup.state === 'Succeeded') revalidatePath('/billing');
    return { topup: topupView(topup, locale, t) };
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }
}

function topupView(topup: FleetTopup, locale: Locale, t: FleetTranslator): TopupView {
  const amount = t('fleet.money.rupees', {
    amount: formatFareMinor(locale, topup.amountMinor),
  });
  const method = t(
    topup.method === 'onepay'
      ? 'fleet.billing.topup.method.onepay'
      : 'fleet.billing.topup.method.lankaqr',
  );

  // The artefacts are on the initiate only — "a redirect URL, a session token and
  // a QR payload are single-use, seconds-lived instruments of one gateway
  // session", so the poll answers without them and the link simply stops being
  // offered rather than being offered dead.
  const continueUrl = topup.redirectUrl ?? topup.paymentLink ?? null;

  return {
    topupId: topup.topupId,
    state: topup.state,
    headline: t('fleet.billing.topup.session', { amount, method }),
    status: statusSentence(topup, t),
    tone:
      topup.state === 'Succeeded'
        ? 'success'
        : topup.state === 'Failed'
          ? 'error'
          : topup.expired
            ? 'warning'
            : 'info',
    continueUrl,
    continueLabel: continueUrl
      ? t(
          topup.method === 'onepay'
            ? 'fleet.billing.topup.continueOnepay'
            : 'fleet.billing.topup.continueLankaqr',
        )
      : null,
    qrPayload: topup.qrPayload ?? null,
  };
}

function statusSentence(topup: FleetTopup, t: FleetTranslator): string {
  if (topup.state === 'Succeeded') return t('fleet.billing.topup.succeeded');
  if (topup.state === 'Failed') return t('fleet.billing.topup.failed');
  if (topup.expired) return t('fleet.billing.topup.expired');
  return t('fleet.billing.topup.pending', { seconds: TOPUP_PENDING_WINDOW_SECONDS });
}

/* ---------------------------------------------------------------------------
 * Pay
 * ------------------------------------------------------------------------ */

/**
 * `POST …/billing/{invoiceId}/pay` — settle this month from the wallet now.
 *
 * The `Idempotency-Key` is a **fresh** one, which is right here and would be wrong
 * on most writes: settlement is "idempotent by construction rather than by header
 * — the money moves under the ledger key `fleet_invoice:{invoiceId}`, which is
 * UNIQUE in `billing.journal_entries`, so a second press posts nothing". A stable
 * header key would add a second, weaker guard over the same money.
 *
 * Two refusals are drawn differently because an operator does two different things
 * about them: `402 insufficient-wallet` is an amount to top up and the invoice is
 * left open; `409 invoice-not-payable` is a month that is already settled or cost
 * nothing, and the button is not drawn for either state in the first place.
 */
export async function payInvoice(
  _state: PayActionState,
  formData: FormData,
): Promise<PayActionState> {
  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);

  const invoiceId = text(formData, 'invoiceId');
  if (!invoiceId) return { message: t('fleet.billing.error.invoiceMissing') };

  let settled: FleetInvoice;
  try {
    const outcome = await mutate<FleetInvoice>({
      method: 'POST',
      org: invoicePayTarget(invoiceId),
      requires: REQUIRES,
    });
    settled = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  revalidatePath('/billing');

  return {
    paid: t('fleet.billing.pay.done', {
      amount: t('fleet.money.rupees', { amount: formatFareMinor(locale, settled.amountMinor) }),
    }),
  };
}
