import Link from 'next/link';

import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { ReportRowView } from './model';
import { ReportDecisionForm, type ReportDecisionLabels } from './ReportDecisionForm';

/**
 * SCR-AP-004's **Vehicle reports — pending review** card.
 *
 * The wireframe's columns are Subject · Reports · Reason · Driver Level · Action.
 * Four of the five are drawn as they are; the fifth is not, and the substitution
 * is deliberate:
 *
 *  - **Driver Level is not on this screen.** A `ReportRow` names a vehicle and
 *    nothing else — no driver, no plate, no level. The level is reputation-svc's
 *    (D-04) and no admin-bff route joins it onto a report, so the column would be
 *    a header over an em dash on every row. **Raised** takes its place, which the
 *    payload does carry and a queue worked from its head needs.
 *  - **A row with no reports cannot exist here.** The sketch's third row (`0`
 *    reports, "No action") is a subject nobody has reported; membership of this
 *    queue *is* having a pending report. The same shape as SCR-AP-003's
 *    auto-verified row, and the same answer: the row is not drawn because the
 *    platform cannot produce it.
 *
 * Both are recorded in the C107 handoff.
 */

export interface ReportQueueLabels {
  readonly heading: string;
  readonly caption: string;
  readonly rule: string;
  readonly subject: string;
  readonly reports: string;
  readonly reason: string;
  readonly raised: string;
  readonly action: string;
  readonly noReason: string;
  readonly suspend: string;
  readonly empty: string;
  /**
   * The row controls' copy. The two accessible names are **not** here: they name
   * the vehicle they act on, so they are built per row in the model.
   */
  readonly decision: Omit<ReportDecisionLabels, 'confirmNamed' | 'dismissNamed'>;
}

export function ReportQueueTable({
  rows,
  labels,
}: {
  rows: readonly ReportRowView[];
  labels: ReportQueueLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.rule}
        </StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.subject}</TH>
            <TH>{labels.reports}</TH>
            <TH>{labels.reason}</TH>
            <TH>{labels.raised}</TH>
            <TH className="sr-only">{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD>
                  <span className="block break-all font-semibold">{row.vehicleId}</span>
                  <Link
                    href={row.suspendHref}
                    className="text-caption text-on-surface-variant underline underline-offset-2"
                  >
                    {labels.suspend}
                  </Link>
                </TD>
                <TD>
                  <StatusPill tone={row.pending.tone}>{row.pending.label}</StatusPill>
                </TD>
                <TD className="max-w-[280px]">{row.reason ?? labels.noReason}</TD>
                <TD className="whitespace-nowrap">{row.raised ?? '—'}</TD>
                <TD className="text-right">
                  <ReportDecisionForm
                    reportId={row.reportId}
                    labels={{
                      ...labels.decision,
                      confirmNamed: row.confirmNamed,
                      dismissNamed: row.dismissNamed,
                    }}
                  />
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
