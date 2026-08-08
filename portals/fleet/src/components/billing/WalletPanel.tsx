import { TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { TopUpForm, type TopUpFormLabels } from './TopUpForm';
import type { WalletMovementRow } from './billing-model';

/**
 * **SCR-FP-010's "Fleet wallet" card** — the balance, the statement and the
 * top-up (US-13.10b).
 *
 * ## Three figures, and one of them is signed
 *
 * `balanceMinor` is `billing.accounts` — the ledger master, never the
 * `billing.wallets` mirror, "which exists for dispatch-svc's hot path and would
 * show an operator a number that lags their own top-up". `outstandingMinor` is Σ
 * of the open invoices. `availableMinor` is the difference and is **signed here
 * where a driver's is floored at zero**: "a fleet that owes more than it holds is
 * exactly the state SCR-FP-010 has to draw, and flooring it would render 'you can
 * cover this' over a shortfall". So it is printed as it comes.
 *
 * ## The statement is on the card because the route puts it there
 *
 * `GET …/wallet` answers a balance **and recent movements** — "SCR-FP-010's
 * balance card and statement". A top-up, a settlement, and the balance each one
 * produced, newest first. It is not a ledger export; that is the invoice CSV.
 *
 * A server component; the top-up form is its one client child.
 */

export interface WalletPanelLabels {
  readonly heading: string;
  readonly balance: string;
  readonly outstanding: string;
  readonly available: string;
  readonly negativeNote: string;
  readonly statementHeading: string;
  readonly statementCaption: string;
  readonly movementKind: string;
  readonly movementWhen: string;
  readonly movementAmount: string;
  readonly movementBalance: string;
  readonly statementEmpty: string;
  readonly updatedAt: string | null;
  readonly topUp: TopUpFormLabels;
}

export interface WalletPanelProps {
  readonly balance: string;
  readonly outstanding: string;
  readonly available: string;
  /** True when the organisation owes more than it holds. */
  readonly short: boolean;
  readonly movements: readonly WalletMovementRow[];
  readonly labels: WalletPanelLabels;
}

export function WalletPanel({
  balance,
  outstanding,
  available,
  short,
  movements,
  labels,
}: WalletPanelProps) {
  return (
    <section
      aria-label={labels.heading}
      className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card lg:w-96"
    >
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <div className="flex flex-col gap-xxs">
        <p className="text-headline font-display text-on-surface tabular-nums">{balance}</p>
        <p className="text-caption text-on-surface-variant">{labels.balance}</p>
      </div>

      <dl className="flex flex-col gap-xxs">
        <Line term={labels.outstanding} value={outstanding} />
        <Line term={labels.available} value={available} tone={short ? 'error' : 'default'} />
      </dl>

      {short ? <p className="text-caption text-error">{labels.negativeNote}</p> : null}

      <TopUpForm labels={labels.topUp} />

      <div className="flex flex-col gap-xs border-t border-surface-variant pt-sm">
        <h3 className="text-body font-semibold">{labels.statementHeading}</h3>

        <Table caption={labels.statementCaption}>
          <THead>
            <TR>
              <TH>{labels.movementKind}</TH>
              <TH>{labels.movementWhen}</TH>
              <TH>{labels.movementAmount}</TH>
              <TH>{labels.movementBalance}</TH>
            </TR>
          </THead>
          <TBody>
            {movements.length === 0 ? (
              <TableEmpty colSpan={4}>{labels.statementEmpty}</TableEmpty>
            ) : (
              movements.map((movement) => (
                <TR key={movement.key}>
                  <TD>{movement.kind}</TD>
                  <TD className="whitespace-nowrap">{movement.when}</TD>
                  <TD
                    className={`whitespace-nowrap tabular-nums ${
                      movement.debit ? 'text-error' : 'text-success'
                    }`}
                  >
                    {movement.amount}
                  </TD>
                  <TD className="whitespace-nowrap tabular-nums">{movement.balanceAfter}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>

        {labels.updatedAt ? (
          <p className="text-caption text-on-surface-variant">{labels.updatedAt}</p>
        ) : null}
      </div>
    </section>
  );
}

function Line({
  term,
  value,
  tone = 'default',
}: {
  term: string;
  value: string;
  tone?: 'default' | 'error';
}) {
  return (
    <div className="flex items-baseline gap-sm">
      <dt className="flex-1 text-body-sm text-on-surface-variant">{term}</dt>
      <dd
        className={`text-body-sm font-semibold tabular-nums ${
          tone === 'error' ? 'text-error' : 'text-on-surface'
        }`}
      >
        {value}
      </dd>
    </div>
  );
}
