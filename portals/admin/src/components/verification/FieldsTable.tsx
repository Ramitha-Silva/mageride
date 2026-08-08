import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import { FieldDecisionForm, type FieldDecisionLabels } from './FieldDecisionForm';
import type { FieldRowView } from './model';

/**
 * SCR-AP-003a's **AI-extracted fields** card: Field · Value · Source · Status ·
 * Action, exactly the wireframe's five columns.
 *
 * The two provenance columns are the point of the screen. `Source` is where the
 * value came from — Gemini Flash 3.0 with its confidence, or the driver's own
 * typing (AL-29, US-2.4a) — and `Status` is what the platform has decided about
 * it. **Only a `Pending` row carries the action pair**: an auto-verified value was
 * never in question, and C063 answers a confirm on one with a `404`, so a button
 * there would offer a decision the platform declines to record.
 */

export interface FieldsTableLabels {
  readonly heading: string;
  readonly engine: string;
  readonly caption: string;
  readonly field: string;
  readonly value: string;
  readonly source: string;
  readonly status: string;
  readonly action: string;
  readonly empty: string;
  readonly note: string;
  /**
   * The row controls' copy. The two accessible names are **not** here: they name
   * the field they act on, so they are built per row where its label is.
   */
  readonly decision: Omit<FieldDecisionLabels, 'confirmNamed' | 'editNamed'>;
}

export function FieldsTable({
  rows,
  subjectId,
  subjectType,
  returnTo,
  labels,
}: {
  rows: readonly FieldRowView[];
  subjectId: string;
  subjectType: string;
  returnTo: string;
  labels: FieldsTableLabels;
}) {
  return (
    <section className="flex min-w-0 flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-xs">
        <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>
        <StatusPill tone="info" dot={false}>
          {labels.engine}
        </StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.field}</TH>
            <TH>{labels.value}</TH>
            <TH>{labels.source}</TH>
            <TH>{labels.status}</TH>
            <TH className="text-right">{labels.action}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={5}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TH scope="row" className={row.decidable ? 'font-semibold text-on-surface' : ''}>
                  {row.label}
                </TH>
                <TD className="break-words">{row.value}</TD>
                <TD>
                  <StatusPill tone={row.source.tone}>{row.source.label}</StatusPill>
                </TD>
                <TD>
                  <StatusPill tone={row.status.tone}>{row.status.label}</StatusPill>
                </TD>
                <TD className="text-right">
                  {row.decidable ? (
                    <FieldDecisionForm
                      subjectId={subjectId}
                      subjectType={subjectType}
                      fieldKey={row.key}
                      value={row.value === '—' ? '' : row.value}
                      returnTo={returnTo}
                      labels={{
                        ...labels.decision,
                        confirmNamed: row.confirmNamed,
                        editNamed: row.editNamed,
                      }}
                    />
                  ) : null}
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
