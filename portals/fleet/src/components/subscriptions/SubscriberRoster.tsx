import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { ConfirmSlipForm, type ConfirmSlipLabels } from './ConfirmSlipForm';
import { DeleteSubscriberForm, type DeleteSubscriberLabels } from './DeleteSubscriberForm';
import { FareCell, type FareCellLabels } from './FareCell';
import { MarkCashForm, type MarkCashLabels } from './MarkCashForm';
import type { SubscriberRowView } from './subscription-model';

/**
 * **SCR-FP-011's "Subscribers · VN-8810"** — items 16 and 17's roster.
 *
 * The wireframe's five columns, and each carries a rule that lives on the wire
 * rather than in this file:
 *
 * | column | what decides it |
 * |---|---|
 * | Passenger | `name` and `mobileMasked`, masked by subscription-svc |
 * | Monthly fare | `monthlyFareMinor`, editable per subscriber (US-23.7) |
 * | Billing cycle | `cycle` — **the next-due date is not on this contract** |
 * | This month | `thisMonthStatus`, subscription-svc's verdict on the Colombo month |
 * | Actions | `muted`, which is AL-25's own flag |
 *
 * **A muted row stays.** US-23.12 keeps an unsubscribed passenger "visible but
 * muted in the Fleet Portal until the owner deletes that subscriber", so the row
 * is rendered at reduced emphasis with Delete in place of the month's verbs — it
 * is never filtered out, and nothing on this screen removes it except the owner's
 * own press.
 *
 * A server component. The four verbs are client components, one per write.
 */

export interface SubscriberRosterLabels {
  readonly heading: string;
  readonly caption: string;
  readonly service: string;
  readonly passenger: string;
  readonly fare: string;
  readonly cycle: string;
  readonly thisMonth: string;
  readonly actions: string;
  readonly empty: string;
  readonly payments: string;
  readonly noMobile: string;
  /** That the next-due date is shown in the passenger's own app, not here. */
  readonly cycleNote: string;
  /** The wireframe's own footnote about cash, slips and muted rows. */
  readonly note: string;
  /** That this money is the owner's and never MageRide's (AL-24, BR-23.10). */
  readonly passThroughNote: string;
  /** Shown in the Actions cell of a row this caller may not act on. */
  readonly ownerOnly: string | null;
  readonly moreRows: string | null;
}

export function SubscriberRoster({
  vehicleId,
  rows,
  mayManage,
  labels,
  fareLabels,
  cashLabels,
  confirmLabels,
  deleteLabels,
}: {
  vehicleId: string;
  rows: readonly SubscriberRowView[];
  /** `canManageSubscribers` — the Owner half of the Epic 23 proxies. */
  mayManage: boolean;
  labels: SubscriberRosterLabels;
  fareLabels: FareCellLabels;
  cashLabels: MarkCashLabels;
  confirmLabels: ConfirmSlipLabels;
  deleteLabels: DeleteSubscriberLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone="info">{labels.service}</StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.passenger}</TH>
            <TH>{labels.fare}</TH>
            <TH>{labels.cycle}</TH>
            <TH>{labels.thisMonth}</TH>
            <TH>{labels.actions}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              // AL-25's muted row: dimmed, still there, and still the owner's to
              // delete. `opacity-60` rather than the sketch's `.45`, because the
              // Delete button inside it has to stay legible in both appearances.
              <TR key={row.key} className={row.muted ? 'opacity-60' : undefined}>
                <TD>
                  <span className="block">{row.passenger}</span>
                  <span className="block text-caption text-on-surface-variant">
                    {row.mobile ?? labels.noMobile}
                  </span>
                </TD>

                <TD>
                  <FareCell
                    vehicleId={vehicleId}
                    subscriberId={row.subscriberId}
                    fare={row.fare}
                    fareInput={row.fareInput}
                    editable={mayManage && row.maySetFare}
                    labels={fareLabels}
                  />
                </TD>

                <TD className="whitespace-nowrap">{row.cycle}</TD>

                <TD>
                  <div className="flex flex-col gap-xs">
                    <StatusPill tone={row.statusTone}>{row.statusLabel}</StatusPill>

                    {mayManage && row.pendingPaymentId ? (
                      <ConfirmSlipForm
                        paymentId={row.pendingPaymentId}
                        slipUrl={row.pendingSlipUrl}
                        labels={confirmLabels}
                      />
                    ) : null}

                    {mayManage && row.mayMarkCash && !row.pendingPaymentId ? (
                      <MarkCashForm
                        vehicleId={vehicleId}
                        subscriberId={row.subscriberId}
                        defaultAmount={row.fareInput}
                        labels={cashLabels}
                      />
                    ) : null}
                  </div>
                </TD>

                <TD>
                  <div className="flex flex-col gap-xs">
                    <Link
                      href={row.paymentsHref}
                      className="text-body-sm text-primary underline underline-offset-2"
                    >
                      {labels.payments}
                    </Link>

                    {row.mayDelete && mayManage ? (
                      <DeleteSubscriberForm
                        vehicleId={vehicleId}
                        subscriberId={row.subscriberId}
                        passenger={row.passenger}
                        labels={deleteLabels}
                      />
                    ) : null}

                    {!mayManage && labels.ownerOnly ? (
                      <span className="text-caption text-on-surface-variant">
                        {labels.ownerOnly}
                      </span>
                    ) : null}
                  </div>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.cycleNote}</p>
      <p className="text-caption text-on-surface-variant">{labels.note}</p>
      <p className="text-caption text-on-surface-variant">{labels.passThroughNote}</p>
      {labels.moreRows ? (
        <p className="text-caption text-warning">{labels.moreRows}</p>
      ) : null}
    </section>
  );
}
