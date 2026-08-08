import Link from 'next/link';

import { StatusPill, type StatusTone } from '@mageride/ui';

/**
 * **SCR-FP-003's "Wallet & next invoice" card** — the balance, what is owed, and
 * the month that will be settled next (US-13.10).
 *
 * ## The button is a link, and that is the whole design of this card
 *
 * The sketch draws "Top up wallet". Topping up is `POST …/wallet/topup`, a payment
 * session with OnePay or LankaQR, a 90-second window and a provider callback — it
 * is SCR-FP-010, which is C115's screen and where the top-up belongs. A dashboard
 * card that could spend an organisation's wallet would be a second billing surface
 * nobody reviewed as one, so the control goes to the screen that owns it.
 *
 * ## It renders for an Owner of an approved organisation, and for nobody else
 *
 * `FleetBillingAccessFilter` gates every route in `fleet-billing.yaml` — reads
 * included — on the Owner sub-role and an APPROVED organisation. Both refusals can
 * be known before the request, so `canReadBilling()` decides whether this card is
 * a card or a sentence, and the sentence says which of the two it is.
 *
 * A server component.
 */

export interface WalletCardLabels {
  readonly heading: string;
  readonly balance: string;
  readonly outstanding: string;
  readonly available: string;
  readonly nextInvoice: string;
  readonly topUp: string;
  readonly modeANote: string;
  /** Shown in place of an invoice when every month is settled. */
  readonly nothingDue: string;
}

export interface WalletCardProps {
  readonly balance: string;
  readonly outstanding: string;
  readonly available: string;
  /** `null` when no invoice is `DUE` or `OVERDUE`. */
  readonly invoice: {
    readonly period: string;
    readonly amount: string;
    readonly status: string;
    readonly statusTone: StatusTone;
    readonly vehicleCount: string | null;
    readonly due: string | null;
  } | null;
  readonly labels: WalletCardLabels;
}

export function WalletCard({
  balance,
  outstanding,
  available,
  invoice,
  labels,
}: WalletCardProps) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-col gap-sm rounded-md border border-outline bg-surface p-sm lg:w-80"
    >
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <div className="flex flex-col gap-xxs">
        <p className="text-headline font-display text-on-surface">{balance}</p>
        <p className="text-caption text-on-surface-variant">{labels.balance}</p>
      </div>

      <dl className="flex flex-col gap-xxs">
        <Line term={labels.outstanding} value={outstanding} />
        {/*
          `availableMinor` is signed — "an organisation that owes more than it
          holds is a state this screen has to draw" — so it is printed as it comes
          rather than floored at zero.
        */}
        <Line term={labels.available} value={available} />
      </dl>

      <div className="flex flex-col gap-xxs border-t border-surface-variant pt-sm">
        {invoice ? (
          <>
            <div className="flex items-center gap-xs">
              <span className="flex-1 text-body-sm text-on-surface-variant">
                {labels.nextInvoice}
              </span>
              <StatusPill tone={invoice.statusTone}>{invoice.status}</StatusPill>
            </div>
            <p className="text-title font-display text-on-surface">{invoice.amount}</p>
            <p className="text-caption text-on-surface-variant">{invoice.period}</p>
            {invoice.vehicleCount ? (
              <p className="text-caption text-on-surface-variant">{invoice.vehicleCount}</p>
            ) : null}
            {invoice.due ? (
              <p className="text-caption text-on-surface-variant">{invoice.due}</p>
            ) : null}
          </>
        ) : (
          <p className="text-body-sm text-on-surface-variant">{labels.nothingDue}</p>
        )}
      </div>

      <Link
        href="/billing"
        className="inline-flex h-10 items-center justify-center rounded-sm border border-outline bg-surface px-md text-body-sm font-body text-on-surface hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      >
        {labels.topUp}
      </Link>

      <p className="text-caption text-on-surface-variant">{labels.modeANote}</p>
    </section>
  );
}

function Line({ term, value }: { term: string; value: string }) {
  return (
    <div className="flex items-baseline gap-sm">
      <dt className="flex-1 text-body-sm text-on-surface-variant">{term}</dt>
      <dd className="text-body-sm font-semibold text-on-surface tabular-nums">{value}</dd>
    </div>
  );
}

/**
 * The card as a Manager, a Viewer or a pending organisation's Owner sees it: one
 * sentence saying whose it is, or what it is waiting on.
 */
export function WalletUnavailable({
  heading,
  reason,
}: {
  heading: string;
  reason: string;
}) {
  return (
    <section
      aria-label={heading}
      className="flex flex-col gap-xs rounded-md border border-outline bg-surface-variant p-sm lg:w-80"
    >
      <h2 className="text-subtitle font-semibold">{heading}</h2>
      <p className="text-body-sm text-on-surface-variant">{reason}</p>
    </section>
  );
}
