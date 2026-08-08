'use client';

import { useActionState } from 'react';

import { Button, Field, StatusPill, Textarea } from '@mageride/ui';

import { decideSubject, type SubjectDecisionState } from '@/server/verification-actions';

import type { StepRowView } from './model';

/**
 * SCR-AP-003a/003c's decision rail: the per-step Verified/Pending breakdown, the
 * reason box, and the two verdicts.
 *
 * ## Approve is disabled until every flagged field is confirmed
 *
 * That is US-2.10a and the DoD's own sentence, and it is stated three times on
 * purpose. Here, so the operator can see the gate rather than discover it; in
 * `decideSubject`, because a disabled button is a fact about a page that may be a
 * minute old; and in admin-bff, which answers `409` and is the only one of the
 * three that is authorization. `approvable` comes from the same read that draws
 * the rail, so the button and the breakdown can never disagree.
 *
 * ## Why the label is not the wireframe's "Confirm all & approve"
 *
 * Because the button does not confirm anything — it posts
 * `POST /v1/admin/verification/{id}/approve`, which *refuses* while a field is
 * pending. A control that named an action it cannot perform, sitting disabled for
 * exactly the reason its label promises to fix, is the one deviation from the
 * sketch this screen makes; SCR-AP-003c's own button ("Approve organisation") is
 * the wording it follows. Recorded in the C106 handoff.
 *
 * ## One form, two buttons
 *
 * The wireframe draws one panel and the two verdicts share the reason box, so
 * they share a form and `intent` says which was pressed. The box is only
 * *required* for a refusal (US-2.15: shown verbatim to the applicant), which is
 * checked in the action rather than with `required` — a browser refusing to
 * submit says "fill this in" in the browser's language, not in the operator's.
 */

export interface DecisionRailLabels {
  readonly heading: string;
  readonly stepsHeading: string;
  readonly reason: string;
  readonly reasonHint: string;
  readonly approve: string;
  readonly reject: string;
  readonly working: string;
  readonly blocked: string;
  readonly audit: string;
}

const INITIAL: SubjectDecisionState = {};

export function DecisionRail({
  subjectId,
  subjectType,
  approvable,
  steps,
  returnTo,
  labels,
}: {
  subjectId: string;
  subjectType: string;
  approvable: boolean;
  steps: readonly StepRowView[];
  /** The queue the verdict returns to, filter and tab intact. */
  returnTo: string;
  labels: DecisionRailLabels;
}) {
  const [state, formAction, pending] = useActionState(decideSubject, INITIAL);

  return (
    <section className="flex w-full shrink-0 flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card lg:w-[260px]">
      <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>

      {steps.length > 0 ? (
        <div className="flex flex-col gap-xxs rounded-md bg-surface-variant p-xs">
          <p className="text-label font-semibold text-on-surface">{labels.stepsHeading}</p>
          <ul className="flex flex-col gap-xxs">
            {steps.map((step) => (
              <li key={step.key} className="flex items-center justify-between gap-xs">
                <span className="text-body-sm text-on-surface">{step.label}</span>
                <StatusPill tone={step.status.tone}>{step.status.label}</StatusPill>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      <form action={formAction} className="flex flex-col gap-sm">
        <input type="hidden" name="subjectId" value={subjectId} />
        <input type="hidden" name="subjectType" value={subjectType} />
        <input type="hidden" name="returnTo" value={returnTo} />
        <input type="hidden" name="approvable" value={String(approvable)} />

        <Field
          label={labels.reason}
          hint={labels.reasonHint}
          {...(state.field === 'reason' && state.message ? { error: state.message } : {})}
        >
          <Textarea name="reason" maxLength={1000} rows={3} />
        </Field>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        <Button
          type="submit"
          name="intent"
          value="approve"
          size="compact"
          disabled={!approvable || pending}
          busy={pending}
          busyLabel={labels.working}
        >
          {labels.approve}
        </Button>

        {!approvable ? (
          <p className="text-caption text-on-surface-variant">{labels.blocked}</p>
        ) : null}

        <Button
          type="submit"
          name="intent"
          value="reject"
          size="compact"
          variant="danger"
          disabled={pending}
        >
          {labels.reject}
        </Button>

        <p className="text-caption text-on-surface-variant">{labels.audit}</p>
      </form>
    </section>
  );
}
