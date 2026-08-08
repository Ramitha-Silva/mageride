import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { PayoutBatchView, PayoutRowView } from './model';

/**
 * SCR-AP-006's "Payouts" tab — AL-58's weekly driver payout run, read-only.
 *
 * ## There is no retry control and there is no route for one
 *
 * C133 removed `POST /v1/admin/payouts/{payoutId}/retry` and its own note says
 * why: a `FAILED` instruction has **already had its debit reversed** — the money
 * is back on the driver's wallet — so there is nothing to re-submit, and the next
 * weekly sweep picks the restored balance up. Where somebody genuinely has to be
 * paid before Sunday, running the batch is that capability. So a failed row shows
 * the reason and nothing to press, which is the honest surface for it.
 *
 * ## Running a sweep out of band is not offered here
 *
 * `POST /v1/admin/payouts/batches` exists and is idempotent on the Colombo date
 * (`409 payout-batch-exists` rather than a second sweep). It is deliberately not
 * wired to a button in this component: the route is unreachable through the
 * gateway today (see the panel's own note and the C108 handoff), and a control
 * that moves every driver's whole balance should not land in the same change that
 * discovers the read half does not resolve. Recorded in the handoff.
 */

export interface PayoutsLabels {
  readonly heading: string;
  readonly note: string;
  readonly batchesHeading: string;
  readonly batchesCaption: string;
  readonly batchRun: string;
  readonly batchStatus: string;
  readonly batchInstructions: string;
  readonly batchTotal: string;
  readonly batchCompleted: string;
  readonly batchesEmpty: string;
  readonly instructionsCaption: string;
  readonly columnDriver: string;
  readonly columnAmount: string;
  readonly columnStatus: string;
  readonly columnAccount: string;
  readonly columnCreated: string;
  readonly columnSettled: string;
  readonly instructionsEmpty: string;
  readonly noRetry: string;
}

export function PayoutsPanel({
  batches,
  payouts,
  labels,
}: {
  batches: readonly PayoutBatchView[];
  payouts: readonly PayoutRowView[];
  labels: PayoutsLabels;
}) {
  return (
    <section className="flex flex-col gap-md">
      <div className="flex flex-col gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.batchesHeading}</h2>
        <p className="text-caption text-on-surface-variant">{labels.note}</p>

        <Table caption={labels.batchesCaption}>
          <THead>
            <TR>
              <TH>{labels.batchRun}</TH>
              <TH>{labels.batchStatus}</TH>
              <TH className="text-right">{labels.batchInstructions}</TH>
              <TH className="text-right">{labels.batchTotal}</TH>
              <TH>{labels.batchCompleted}</TH>
            </TR>
          </THead>
          <TBody>
            {batches.length === 0 ? (
              <TableEmpty colSpan={5}>{labels.batchesEmpty}</TableEmpty>
            ) : (
              batches.map((batch) => (
                <TR key={batch.key}>
                  <TD className="whitespace-nowrap">{batch.runDate ?? '—'}</TD>
                  <TD>
                    <StatusPill tone={batch.status.tone} dot={false}>
                      {batch.status.label}
                    </StatusPill>
                  </TD>
                  <TD className="text-right tabular-nums">{batch.instructions}</TD>
                  <TD className="text-right tabular-nums">{batch.total}</TD>
                  <TD className="whitespace-nowrap">{batch.completed ?? '—'}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>
      </div>

      <div className="flex flex-col gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

        <Table caption={labels.instructionsCaption}>
          <THead>
            <TR>
              <TH>{labels.columnDriver}</TH>
              <TH className="text-right">{labels.columnAmount}</TH>
              <TH>{labels.columnStatus}</TH>
              <TH>{labels.columnAccount}</TH>
              <TH>{labels.columnCreated}</TH>
              <TH>{labels.columnSettled}</TH>
            </TR>
          </THead>
          <TBody>
            {payouts.length === 0 ? (
              <TableEmpty colSpan={6}>{labels.instructionsEmpty}</TableEmpty>
            ) : (
              payouts.map((payout) => (
                <TR key={payout.key}>
                  <TD className="font-mono text-caption break-all">{payout.driverId}</TD>
                  <TD className="text-right tabular-nums">{payout.amount}</TD>
                  <TD>
                    <StatusPill tone={payout.status.tone} dot={false}>
                      {payout.status.label}
                    </StatusPill>
                    {payout.failureReason ? (
                      <span className="mt-xxs block text-caption text-on-surface-variant">
                        {payout.failureReason}
                      </span>
                    ) : null}
                  </TD>
                  <TD>{payout.account ? `••••${payout.account}` : '—'}</TD>
                  <TD className="whitespace-nowrap">{payout.created ?? '—'}</TD>
                  <TD className="whitespace-nowrap">{payout.settled ?? '—'}</TD>
                </TR>
              ))
            )}
          </TBody>
        </Table>

        <p className="text-caption text-on-surface-variant">{labels.noRetry}</p>
      </div>
    </section>
  );
}
