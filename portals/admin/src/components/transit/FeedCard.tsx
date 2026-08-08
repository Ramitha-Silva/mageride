import Link from 'next/link';

import { StatusPill, Table, TBody, TD, TH, THead, TR } from '@mageride/ui';

import { ActivateForm, type ActivateLabels } from './ActivateForm';
import { FeedPoller } from './FeedPoller';
import type { FeedCardView } from './model';

/**
 * SCR-AP-016's middle zone: the status stepper, the preview and the failure
 * report, drawn as **one card** because they are one feed at three points in its
 * life.
 *
 * D2 lists them as separate rows of its table and separate states of the screen —
 * `validating`, `validated-preview`, `failed-report`, `active-idle` — but they are
 * never simultaneous, and a screen with three cards would have two empty ones
 * whatever the feed was doing. The stepper is always drawn (it is the same three
 * positions in every state); what changes underneath it is the preview, the error
 * summary, or neither.
 *
 * ## The counts grid renders what the feed had, not what a feed should have
 *
 * `shapes.txt` is optional and a feed without it produces no `shapes` key — so no
 * `shapes` column, rather than a `0` that reads as an empty file. The same is true
 * of `calendar` versus `calendar_dates`: BR-32.1 requires one *or* the other, and
 * printing a zero for the one the provider did not use would look like a defect in
 * a feed that is correct.
 *
 * ## Warnings sit beside Activate, never instead of it
 *
 * BR-32.1's line between an error and a warning is the line between "this dataset
 * would break route matching" and "somebody should look at this" — a stop 400 km
 * out to sea against a service window ending in three weeks. Warnings therefore
 * never block activation and are collapsed by default; a feed with twelve renamed
 * stops is still the feed the country runs on.
 */

export interface FeedCardLabels {
  readonly heading: string;
  readonly stepperLabel: string;
  readonly version: string;
  readonly noVersion: string;
  readonly serviceWindow: string;
  readonly noWindow: string;
  readonly countsCaption: string;
  readonly noCounts: string;
  readonly validatingNote: string;
  readonly noWarnings: string;
  readonly failedHeading: string;
  readonly failedBody: string;
  readonly reportCsv: string;
  readonly reportJson: string;
  readonly liveNote: string;
  readonly archivedNote: string;
  readonly activate: ActivateLabels;
}

const STEP_TONE = {
  done: 'border-primary text-primary',
  current: 'border-primary text-primary',
  todo: 'border-outline text-on-surface-variant',
} as const;

export function FeedCard({
  feed,
  outgoing,
  reportHref,
  labels,
}: {
  feed: FeedCardView;
  /** The version this one would replace, or `null` when nothing is live. */
  outgoing: string | null;
  /** This portal's relay of `…/uploads/{id}/report`, without a format. */
  reportHref: string;
  labels: FeedCardLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      {/* Only while a verdict is outstanding. See `FeedPoller`. */}
      <FeedPoller active={feed.pending} />

      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
        <StatusPill tone={feed.statusTone}>{feed.statusLabel}</StatusPill>
        <span className="font-mono text-caption break-all text-on-surface-variant">
          {feed.label}
        </span>
      </div>

      <ol aria-label={labels.stepperLabel} className="flex flex-wrap items-center gap-xs">
        {feed.steps.map((step) => (
          <li
            key={step.id}
            aria-current={step.state === 'current' ? 'step' : undefined}
            className={`rounded-full border px-sm py-xxs text-caption ${
              step.failed ? 'border-error text-error' : STEP_TONE[step.state]
            }`}
          >
            {step.label}
          </li>
        ))}
      </ol>

      {feed.pending ? (
        <p className="text-body-sm text-on-surface-variant">{labels.validatingNote}</p>
      ) : null}

      <dl className="flex flex-wrap gap-md">
        <div>
          <dt className="text-caption text-on-surface-variant">{labels.version}</dt>
          <dd className="text-body-sm text-on-surface">
            {feed.feedInfoVersion ?? labels.noVersion}
          </dd>
        </div>
        <div>
          <dt className="text-caption text-on-surface-variant">{labels.serviceWindow}</dt>
          <dd className="text-body-sm text-on-surface">{feed.serviceWindow ?? labels.noWindow}</dd>
        </div>
      </dl>

      {feed.counts.length === 0 ? (
        <p className="text-body-sm text-on-surface-variant">{labels.noCounts}</p>
      ) : (
        <Table caption={labels.countsCaption}>
          <THead>
            <TR>
              {feed.counts.map((count) => (
                <TH key={count.key}>{count.label}</TH>
              ))}
            </TR>
          </THead>
          <TBody>
            <TR>
              {feed.counts.map((count) => (
                <TD key={count.key}>{count.value}</TD>
              ))}
            </TR>
          </TBody>
        </Table>
      )}

      {feed.status === 'failed' ? (
        <div
          role="alert"
          className="flex flex-col gap-xs rounded-md border border-error/40 bg-error/10 p-sm"
        >
          <p className="text-body-sm font-semibold text-error">{labels.failedHeading}</p>
          <p className="text-body-sm text-on-surface">{labels.failedBody}</p>

          <ul className="flex list-disc flex-col gap-xxs ps-md text-body-sm text-on-surface">
            {feed.errors.map((error) => (
              <li key={error}>{error}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {feed.warnings.length > 0 ? (
        <details className="rounded-md border border-warning/40 bg-warning/10 p-sm">
          <summary className="cursor-pointer text-body-sm text-on-surface">
            {feed.warningsSummary}
          </summary>
          <ul className="flex list-disc flex-col gap-xxs ps-md pt-xs text-body-sm text-on-surface">
            {feed.warnings.map((warning) => (
              <li key={warning}>{warning}</li>
            ))}
          </ul>
        </details>
      ) : feed.status === 'validated' ? (
        <p className="text-caption text-on-surface-variant">{labels.noWarnings}</p>
      ) : null}

      <div className="flex flex-wrap items-center gap-sm">
        {feed.activatable ? (
          <ActivateForm
            feedVersionId={feed.feedVersionId}
            incoming={feed.label}
            outgoing={outgoing}
            rollback={feed.rollback}
            labels={labels.activate}
          />
        ) : null}

        {/*
          Offered whatever the verdict, and not only on a failure: a validated feed
          has warnings worth reading in full, and BR-32.1's report carries both.
          Two formats because they answer two questions — CSV is what an operator
          fixes the feed from, JSON is what somebody diffs between two uploads.
        */}
        <Link
          href={`${reportHref}?format=csv`}
          prefetch={false}
          className="text-body-sm text-primary underline"
        >
          {labels.reportCsv}
        </Link>
        <Link
          href={`${reportHref}?format=json`}
          prefetch={false}
          className="text-body-sm text-primary underline"
        >
          {labels.reportJson}
        </Link>

        {feed.status === 'active' ? (
          <span className="text-caption text-on-surface-variant">{labels.liveNote}</span>
        ) : null}
        {feed.status === 'archived' ? (
          <span className="text-caption text-on-surface-variant">{labels.archivedNote}</span>
        ) : null}
      </div>
    </section>
  );
}
