import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { SettlementRailView, SettlementTotalsView } from './model';

/**
 * SCR-AP-006's first card: OnePay / LankaQR settlement against the ledger, per
 * rail.
 *
 * **There is no bank-transfer row and there cannot be one** (AL-05). The wireframe
 * says so in its own note and admin-bff answers `400` to a third `?method=` — what
 * the two rails settle is wallet top-ups, because AL-57 removed OnePay and
 * platform-merchant LankaQR as *ride* payment methods, so `billing.topups` is the
 * whole of "gateway settlement" on this platform.
 *
 * **Δ the wireframe's "OnePay (+5%)" label.** The surcharge was removed with the
 * rail's ride-payment role (AL-57/AL-59); printing it here would describe a fee
 * nothing charges. The rail is named and the percentage is not.
 *
 * The `Δ` column is the point of the screen: zero is what reconciled means, so it
 * is the success tone and every other value is the error tone. There is no middle
 * band, because there is no amount of unexplained money that is nearly fine.
 */

export interface SettlementLabels {
  readonly heading: string;
  readonly caption: string;
  readonly window: string;
  readonly gateway: string;
  readonly sessions: string;
  readonly settled: string;
  readonly posted: string;
  readonly variance: string;
  readonly action: string;
  readonly investigate: string;
  readonly empty: string;
  readonly noBankTransfer: string;
  readonly totals: string;
}

export function SettlementCard({
  totals,
  rails,
  labels,
}: {
  totals: SettlementTotalsView;
  rails: readonly SettlementRailView[];
  labels: SettlementLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <span className="flex-1" />
        <StatusPill tone={totals.variance.tone}>{totals.variance.label}</StatusPill>
        {totals.hasExceptions ? (
          <StatusPill tone="warning">{totals.exceptions}</StatusPill>
        ) : null}
      </div>

      {totals.window ? (
        <p className="text-caption text-on-surface-variant">
          {labels.window} {totals.window}
        </p>
      ) : null}

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.gateway}</TH>
            <TH className="text-right">{labels.sessions}</TH>
            <TH className="text-right">{labels.settled}</TH>
            <TH className="text-right">{labels.posted}</TH>
            <TH className="text-right">{labels.variance}</TH>
            <TH>{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rails.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rails.map((rail) => (
              <TR key={rail.key}>
                <TD className="font-semibold">{rail.method}</TD>
                <TD className="text-right tabular-nums">{rail.settled}</TD>
                <TD className="text-right tabular-nums">{rail.settledMoney}</TD>
                <TD className="text-right tabular-nums">{rail.postedMoney}</TD>
                <TD className="text-right">
                  <StatusPill tone={rail.variance.tone} dot={false}>
                    {rail.variance.label}
                  </StatusPill>
                </TD>
                <TD>
                  <Link
                    href={rail.investigateHref}
                    aria-label={rail.investigateNamed}
                    className="text-body-sm underline underline-offset-2"
                  >
                    {labels.investigate}
                  </Link>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.noBankTransfer}</p>
      <p className="text-caption text-on-surface-variant">{labels.totals}</p>
    </section>
  );
}
