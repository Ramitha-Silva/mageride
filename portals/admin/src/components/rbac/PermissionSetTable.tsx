import { StatusPill, TBody, TD, TH, THead, TR, Table, TableEmpty } from '@mageride/ui';

import type { PermissionRowView } from './model';

/**
 * The wireframe's "Permission set" card — URD §2.3, **read-only**.
 *
 * The sketch draws toggles. There is no route that writes a cell and there is not
 * going to be one: `getPermissionMatrix` is a `GET` with no `PUT` beside it because
 * "the matrix is a specification, not configuration. A Super Admin who could edit
 * it could grant themselves something URD §2.3 forbids, which is the one thing the
 * matrix exists to prevent. Changing it is a spec change followed by a deploy."
 *
 * So a toggle would be a control that either did nothing or lied about doing
 * something. The cell is drawn as the spec prints it — `✅`, `◐ on tickets`,
 * `⚙ rates` — with the capabilities in words beside it, and the note says where
 * the rule lives. US-21.3 asks for editable permission sets and is recorded as a
 * gap in the C108 handoff rather than faked here.
 */

export interface PermissionSetLabels {
  readonly heading: string;
  readonly note: string;
  readonly caption: string;
  readonly area: string;
  readonly cell: string;
  readonly capabilities: string;
  readonly empty: string;
}

export function PermissionSetTable({
  rows,
  labels,
}: {
  rows: readonly PermissionRowView[];
  labels: PermissionSetLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone="neutral">{labels.note}</StatusPill>
      </div>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.area}</TH>
            <TH>{labels.cell}</TH>
            <TH>{labels.capabilities}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={3}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.key}>
                <TD className="font-mono text-caption break-all">{row.area}</TD>
                <TD>
                  <StatusPill tone={row.tone} dot={false}>
                    {row.symbol}
                  </StatusPill>
                </TD>
                <TD>
                  {row.grants}
                  {row.qualifier ? (
                    <span className="mt-xxs block text-caption text-on-surface-variant">
                      {row.qualifier}
                    </span>
                  ) : null}
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>
    </section>
  );
}
