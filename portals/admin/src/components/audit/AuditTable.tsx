import { TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { AuditRowView } from './model';

/**
 * SCR-AP-009's table — the immutable admin-action and permission-change log.
 *
 * **There is no control in this component and there must not be one.** The
 * contract states it in a line: "Append-only — there is no write route here." So
 * the Auditor's read-only requirement (US-21.7) is not enforced by hiding buttons
 * from a role; there is nothing on the platform to hide. `test/audit-screen.test.tsx`
 * asserts the whole `/audit-log` tree calls no mutation, which is the executable
 * form of that sentence.
 *
 * The change column prints `before` and `after` as the JSON they are stored as.
 * They are free-form — whatever the handler knew about the entity — so a screen
 * that tried to render them as a field-by-field diff would be guessing at the
 * shape of every entity on the platform. An audit trail may be terse; it may not
 * paraphrase.
 */

export interface AuditTableLabels {
  readonly caption: string;
  readonly when: string;
  readonly actor: string;
  readonly role: string;
  readonly action: string;
  readonly target: string;
  readonly change: string;
  readonly empty: string;
}

export function AuditTable({
  rows,
  labels,
}: {
  rows: readonly AuditRowView[];
  labels: AuditTableLabels;
}) {
  return (
    <Table caption={labels.caption}>
      <THead>
        <TR>
          <TH>{labels.when}</TH>
          <TH>{labels.actor}</TH>
          <TH>{labels.role}</TH>
          <TH>{labels.action}</TH>
          <TH>{labels.target}</TH>
          <TH>{labels.change}</TH>
        </TR>
      </THead>
      <TBody>
        {rows.length === 0 ? (
          <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
        ) : (
          rows.map((row) => (
            <TR key={row.key}>
              <TD className="whitespace-nowrap">{row.when ?? '—'}</TD>
              <TD className="font-mono text-caption break-all">
                {row.actorId}
                {row.ip ? (
                  <span className="mt-xxs block text-caption text-on-surface-variant">
                    {row.ip}
                  </span>
                ) : null}
              </TD>
              <TD>{row.role ?? '—'}</TD>
              {/* Never translated: it is the string the log holds and the string
                  `?action=` filters on. */}
              <TD className="font-mono text-caption break-all">{row.action}</TD>
              <TD className="font-mono text-caption break-all">
                {row.target ?? '—'}
                {row.targetType ? (
                  <span className="mt-xxs block text-caption text-on-surface-variant">
                    {row.targetType}
                  </span>
                ) : null}
              </TD>
              <TD>
                {row.change ? (
                  <pre className="max-w-[280px] overflow-x-auto text-caption whitespace-pre-wrap text-on-surface-variant">
                    {row.change}
                  </pre>
                ) : (
                  '—'
                )}
              </TD>
            </TR>
          ))
        )}
      </TBody>
    </Table>
  );
}
