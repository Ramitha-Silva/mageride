import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { RequestDecision, type RequestDecisionLabels } from './RequestDecision';
import type { RequestRowView } from './subscription-model';

/**
 * **SCR-FP-011's "Incoming requests · VN-8810"** — item 15's per-vehicle queue.
 *
 * Per vehicle is the whole point (AL-23): `subscription.access_requests` carries a
 * `vehicle_id`, there is no fleet-wide queue route on any contract, and a driver
 * sees the same queue for the same vehicle in the Driver App (SCR-DA-028). So the
 * heading names the vehicle rather than the fleet, and the pending count is that
 * vehicle's.
 *
 * The **Mobile / ID** column shows the number exactly as subscription-svc masked
 * it — `PhoneMask.Mask` runs on that side and this portal never sees the digits —
 * beside the passenger id, which is the only stable identifier a request carries.
 *
 * A server component; each row's two verbs are the client component beside it.
 */

export interface RequestQueueLabels {
  readonly heading: string;
  readonly caption: string;
  readonly pendingCount: string;
  readonly passenger: string;
  readonly contact: string;
  readonly requested: string;
  readonly action: string;
  readonly empty: string;
  readonly note: string;
  readonly viewerNotice: string | null;
  readonly noMobile: string;
}

export function RequestQueue({
  vehicleId,
  rows,
  mayDecide,
  reasonMaxLength,
  labels,
  decisionLabels,
}: {
  vehicleId: string;
  rows: readonly RequestRowView[];
  mayDecide: boolean;
  reasonMaxLength: number;
  labels: RequestQueueLabels;
  decisionLabels: RequestDecisionLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-subtitle font-semibold">{labels.heading}</h2>
        {rows.length > 0 ? (
          <StatusPill tone="warning">{labels.pendingCount}</StatusPill>
        ) : null}
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.passenger}</TH>
            <TH>{labels.contact}</TH>
            <TH>{labels.requested}</TH>
            <TH>{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={4}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>{row.passenger}</TD>
                <TD>
                  <span className="block whitespace-nowrap">{row.mobile ?? labels.noMobile}</span>
                  <span className="block font-mono text-caption break-all text-on-surface-variant">
                    {row.passengerId}
                  </span>
                </TD>
                <TD className="whitespace-nowrap">{row.requested}</TD>
                <TD>
                  {mayDecide ? (
                    <RequestDecision
                      vehicleId={vehicleId}
                      requestId={row.requestId}
                      passenger={row.passenger}
                      reasonMaxLength={reasonMaxLength}
                      labels={decisionLabels}
                    />
                  ) : (
                    <span className="text-caption text-on-surface-variant">
                      {labels.viewerNotice}
                    </span>
                  )}
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.note}</p>
    </section>
  );
}
