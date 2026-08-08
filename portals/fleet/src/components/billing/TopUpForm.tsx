'use client';

import { useState } from 'react';
import { useActionState } from 'react';

import { Button, Field, Input, StatusPill } from '@mageride/ui';

import { TOPUP_METHODS } from '@/api/billing';
import { checkTopup, topUpWallet, type TopupActionState, type TopupView } from '@/server/billing-actions';

/**
 * **SCR-FP-010's "Top up wallet"** (US-13.10b, AL-05, AL-15).
 *
 * ## Two rails, and the sketch's three rows are two of them
 *
 * `web_fleet.html` lists "💳 Card", "OnePay" and "🏦 LankaQR (Pay deep link)".
 * `topupFleetWallet`'s `method` admits **`onepay` and `lankaqr`**, and
 * `ck_fleet_topups_method` refuses anything else — so a card is not a third
 * method: OnePay *is* the card rail ("`Onepay:ApiKey` unset ⇒ the card rail
 * answers 503"), and a card is entered on OnePay's own hosted page. The radio says
 * so, which is better than an option that would post nowhere.
 *
 * **Bank transfer is not offered anywhere, on any surface (AL-05)**, and the
 * caption says it rather than leaving an operator looking for it.
 *
 * ## Nothing on this form credits the wallet
 *
 * Submitting opens a payment session. The credit happens on the provider's
 * callback as a balanced double-entry journal entry (D-09) — "a session the
 * gateway accepted has moved no money, and treating it as a credit is how a
 * balance grows by abandoning a payment page". So the form ends with a link to
 * finish the payment and a **Check payment** button, not with a new balance.
 *
 * ## Check is a button, not a timer
 *
 * The 90-second window (D6' §7.1) is a window to *complete a session in*, and the
 * operator is completing it on a bank app or a hosted card page — often on another
 * device. A page polling in the background would be asking a question nobody is
 * waiting on. One press after paying is one request.
 */

export interface TopUpFormLabels {
  readonly heading: string;
  readonly amount: string;
  readonly amountHint: string;
  readonly method: string;
  readonly onepay: string;
  readonly onepayHint: string;
  readonly lankaqr: string;
  readonly lankaqrHint: string;
  readonly noBankTransfer: string;
  readonly required: string;
  readonly submit: string;
  readonly submitting: string;
  readonly check: string;
  readonly checking: string;
  readonly qrHeading: string;
  readonly qrHint: string;
}

const INITIAL: TopupActionState = {};

export function TopUpForm({ labels }: { labels: TopUpFormLabels }) {
  const [state, formAction, pending] = useActionState(topUpWallet, INITIAL);

  // The polled session replaces the initiated one. It carries no redirect URL and
  // no QR payload — those are single-use instruments of one gateway session and
  // were never stored — so the link is dropped rather than offered dead.
  const [polled, setPolled] = useState<TopupView | null>(null);
  const [checking, setChecking] = useState(false);

  const session = polled ?? state.topup ?? null;

  const check = (topupId: string) => {
    setChecking(true);
    void checkTopup(topupId)
      .then((answer) => {
        if (answer.topup) setPolled(answer.topup);
      })
      .finally(() => setChecking(false));
  };

  return (
    <div id="top-up" className="flex flex-col gap-sm border-t border-surface-variant pt-sm">
      <h3 className="text-body font-semibold">{labels.heading}</h3>

      <form action={formAction} className="flex flex-col gap-sm">
        <Field
          label={labels.amount}
          hint={labels.amountHint}
          required
          requiredLabel={labels.required}
          {...(state.field === 'amount' && state.message ? { error: state.message } : {})}
        >
          <Input name="amount" inputMode="decimal" autoComplete="off" required />
        </Field>

        <fieldset className="flex flex-col gap-xxs">
          <legend className="pb-xxs text-body-sm text-on-surface-variant">{labels.method}</legend>

          {/*
            Mounted from the contract's own enum rather than from two literals, so
            a rail the platform stops admitting cannot survive here as a radio.
          */}
          {TOPUP_METHODS.map((method) => (
            <label key={method} className="flex items-start gap-xs text-body-sm">
              <input
                type="radio"
                name="method"
                value={method}
                defaultChecked={method === 'onepay'}
                className="mt-xxs size-xs accent-primary"
              />
              <span>
                {method === 'onepay' ? labels.onepay : labels.lankaqr}
                <span className="block text-caption text-on-surface-variant">
                  {method === 'onepay' ? labels.onepayHint : labels.lankaqrHint}
                </span>
              </span>
            </label>
          ))}

          {state.field === 'method' && state.message ? (
            <p role="alert" className="text-body-sm text-error">
              {state.message}
            </p>
          ) : null}
        </fieldset>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        <Button
          type="submit"
          size="compact"
          busy={pending}
          busyLabel={labels.submitting}
          className="self-start"
        >
          {labels.submit}
        </Button>
      </form>

      {session ? (
        <div className="flex flex-col gap-xs rounded-md bg-surface-variant p-sm">
          <div className="flex flex-wrap items-center gap-xs">
            <span className="flex-1 text-body-sm font-semibold">{session.headline}</span>
            <StatusPill tone={session.tone}>{session.status}</StatusPill>
          </div>

          {session.continueUrl && session.continueLabel ? (
            // A new tab, so this screen — and the Check button on it — survives the
            // trip to the gateway.
            <a
              href={session.continueUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex h-10 items-center justify-center self-start rounded-sm bg-primary px-md text-body-sm font-body text-on-primary hover:bg-primary/90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
            >
              {session.continueLabel}
            </a>
          ) : null}

          {session.qrPayload ? (
            <div className="flex flex-col gap-xxs">
              <span className="text-caption text-on-surface-variant">{labels.qrHeading}</span>
              {/*
                The payload as text, not as a rendered code. An EMVCo payload's
                merchant fields and CRC belong to the acquiring bank and are handed
                over whole; drawing it as a bitmap would put a QR encoder in the
                browser bundle for a fallback the AL-15 deep link exists to avoid.
              */}
              <code className="rounded-sm bg-surface p-xs text-caption break-all">
                {session.qrPayload}
              </code>
              <span className="text-caption text-on-surface-variant">{labels.qrHint}</span>
            </div>
          ) : null}

          <Button
            variant="ghost"
            size="compact"
            className="self-start"
            busy={checking}
            busyLabel={labels.checking}
            onClick={() => check(session.topupId)}
          >
            {labels.check}
          </Button>
        </div>
      ) : null}

      <p className="text-caption text-error">{labels.noBankTransfer}</p>
    </div>
  );
}
