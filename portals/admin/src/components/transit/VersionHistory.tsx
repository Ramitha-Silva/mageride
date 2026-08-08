import Link from 'next/link';

import { StatusPill, Table, TableEmpty, TBody, TD, TH, THead, TR } from '@mageride/ui';

import { ActivateForm, type ActivateLabels } from './ActivateForm';
import type { VersionRowView } from './model';

/**
 * SCR-AP-016's version history (US-28.3) — every uploaded feed, newest first, with
 * **Re-activate · Report · Zip**.
 *
 * ## Re-activate is drawn on exactly the rows it can work on
 *
 * `validated` and `archived`. Not `active` — there is nothing to swap and
 * transit-svc answers `409 feed-already-active` — and emphatically not `failed`:
 * "failed feeds can never be activated" is the wireframe's own note and BR-32.1's
 * rule, so a disabled button there would be a control promising that a fix exists.
 * The 409s are still the rule; this is what stops an operator finding out that way.
 *
 * ## The uploader is an id and the wireframe's email cannot be produced
 *
 * `FeedVersion.uploadedBy` is a user id. iam-svc exposes no route that resolves an
 * internal account to a name or an address — C108 found the same gap building
 * SCR-AP-008, where the whole user *directory* had to be dropped for it — so the
 * sketch's `admin@mageride.lk` has nothing behind it. The id is shown instead,
 * which is the value an auditor matches against `audit.events.actor_id` anyway.
 * Recorded in the C110 handoff.
 *
 * ## Nothing is silently dropped off the end
 *
 * One page of a hundred, and where there are more the table says so. A history
 * that quietly stopped at its page size would make "roll back to the feed we ran
 * in March" fail with no explanation, and C108's audit export established the
 * house rule: a cap is stated on the screen where it bites.
 */

export interface VersionHistoryLabels {
  readonly heading: string;
  readonly caption: string;
  readonly version: string;
  readonly file: string;
  readonly uploaded: string;
  readonly routes: string;
  readonly status: string;
  readonly actions: string;
  readonly empty: string;
  readonly report: string;
  readonly zip: string;
  readonly capped: string;
  readonly none: string;
  readonly activate: ActivateLabels;
}

export function VersionHistory({
  rows,
  outgoing,
  screenPath,
  reportPath,
  zipPath,
  capped,
  labels,
}: {
  rows: readonly VersionRowView[];
  /** The live version's name, for the confirm dialog every row's button opens. */
  outgoing: string | null;
  screenPath: string;
  /** `${reportPath}/{feedVersionId}` is this portal's relay of the row-level report. */
  reportPath: string;
  /** `${zipPath}/{feedVersionId}` relays the 302 to the signed object-storage URL. */
  zipPath: string;
  /** Whether admin-bff had more versions than this page carries. */
  capped: boolean;
  labels: VersionHistoryLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.version}</TH>
            <TH>{labels.file}</TH>
            <TH>{labels.uploaded}</TH>
            <TH>{labels.routes}</TH>
            <TH>{labels.status}</TH>
            <TH>{labels.actions}</TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.feedVersionId} selected={row.selected}>
                <TD>
                  <Link
                    href={`${screenPath}?feed=${encodeURIComponent(row.feedVersionId)}`}
                    className="font-mono break-all text-primary underline"
                  >
                    {row.label}
                  </Link>
                </TD>
                <TD className="break-all">{row.fileName}</TD>
                <TD>
                  <span className="block">{row.uploadedAt ?? labels.none}</span>
                  <span className="block font-mono text-caption break-all text-on-surface-variant">
                    {row.uploadedBy}
                  </span>
                </TD>
                <TD>{row.routes ?? labels.none}</TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.statusLabel}</StatusPill>
                </TD>
                <TD>
                  <div className="flex flex-wrap items-center gap-xs">
                    {row.activatable ? (
                      <ActivateForm
                        feedVersionId={row.feedVersionId}
                        incoming={row.label}
                        outgoing={outgoing}
                        rollback={row.rollback}
                        labels={labels.activate}
                        compact
                      />
                    ) : null}

                    <Link
                      href={`${reportPath}/${row.feedVersionId}?format=csv`}
                      prefetch={false}
                      className="text-primary underline"
                    >
                      {labels.report}
                    </Link>
                    <Link
                      href={`${zipPath}/${row.feedVersionId}`}
                      prefetch={false}
                      className="text-primary underline"
                    >
                      {labels.zip}
                    </Link>
                  </div>
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      {capped ? <p className="text-caption text-on-surface-variant">{labels.capped}</p> : null}
    </section>
  );
}
