import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { ConfirmSlipForm, type ConfirmSlipLabels } from './ConfirmSlipForm';
import type { PaymentRowView } from './subscription-model';

/**
 * **SCR-FP-012's "Ledger · Ramith de Silva · VN-8810"** — item 16i's
 * per-subscriber, per-vehicle payment history (US-23.10).
 *
 * Every rail the platform can record appears here and none is offered: a
 * subscription payment is initiated by the passenger in their own app
 * (SCR-PA-025a), and the only payment this console *writes* is the owner's cash
 * mark on SCR-FP-011. What the owner does on this screen is **confirm** — a
 * transfer slip that has been waiting since the passenger uploaded it — and
 * `canConfirm` is the row's own `pending_verification`.
 *
 * The **Date** column is `paidAt` and is empty until a payment is settled, which
 * is the honest reading: an initiated LankaQR session and an unconfirmed slip have
 * no date on which money arrived, and printing the month's own date there would
 * put a settlement date on a month nobody has paid.
 *
 * A server component.
 */

export interface PaymentLedgerLabels {
  readonly heading: string;
  readonly caption: string;
  readonly month: string;
  readonly date: string;
  readonly method: string;
  readonly amount: string;
  readonly status: string;
  readonly action: string;
  readonly empty: string;
  readonly notPaidYet: string;
  /** That confirming or marking cash pushes Paid to the passenger's app. */
  readonly note: string;
  /** That this money is the owner's and never MageRide's (AL-24, BR-23.10). */
  readonly passThroughNote: string;
  readonly moreRows: string | null;
}

export function PaymentLedger({
  rows,
  mayConfirm,
  labels,
  confirmLabels,
}: {
  rows: readonly PaymentRowView[];
  /** `canManageSubscribers` — `POST …/payments/{id}/confirm` is Owner-only. */
  mayConfirm: boolean;
  labels: PaymentLedgerLabels;
  confirmLabels: ConfirmSlipLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.month}</TH>
            <TH>{labels.date}</TH>
            <TH>{labels.method}</TH>
            <TH>{labels.amount}</TH>
            <TH>{labels.status}</TH>
            <TH>{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD className="whitespace-nowrap">{row.month}</TD>
                <TD className="whitespace-nowrap">
                  {row.when ?? (
                    <span className="text-on-surface-variant">{labels.notPaidYet}</span>
                  )}
                </TD>
                <TD>{row.method}</TD>
                <TD className="whitespace-nowrap tabular-nums">{row.amount}</TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.statusLabel}</StatusPill>
                </TD>
                <TD>
                  {row.mayConfirm && mayConfirm ? (
                    <ConfirmSlipForm
                      paymentId={row.paymentId}
                      slipUrl={row.slipUrl}
                      labels={confirmLabels}
                    />
                  ) : null}
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.note}</p>
      <p className="text-caption text-on-surface-variant">{labels.passThroughNote}</p>
      {labels.moreRows ? <p className="text-caption text-warning">{labels.moreRows}</p> : null}
    </section>
  );
}
