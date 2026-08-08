'use client';

import { useActionState } from 'react';

import { Button } from '@mageride/ui';

import { decideReport, type ReportDecisionState } from '@/server/moderation-actions';

/**
 * The verdict pair one pending report carries (US-12.6).
 *
 * ## Why the destructive button says "Confirm" and not the wireframe's "Delist 24h"
 *
 * Because delisting is not what this button does. `POST
 * /v1/admin/reports/{id}/resolve` records **one moderator's verdict on one
 * report**; the *third* confirmation is what removes the vehicle, decided inside
 * safety-svc's own transaction. A control labelled "Delist 24h" would promise two
 * things the platform does not do — that this press delists, and that the
 * delisting expires after a day — and the operator pressing it on a vehicle's
 * first report would be told they had done something that had not happened.
 *
 * What the screen does instead is state the rule beside the queue and report the
 * count back after each confirmation, which is the only moment it is a fact.
 * Recorded in the C107 handoff.
 *
 * ## No note box
 *
 * `resolve` takes an optional `note` and the wireframe draws two buttons. The
 * report's own text is the evidence, the verdict is the decision, and both are in
 * the `audit.events` row admin-bff writes — a third free-text field on every row
 * of a working queue buys none of that.
 *
 * Every string arrives translated, as a prop.
 */

export interface ReportDecisionLabels {
  readonly confirm: string;
  /** "Confirm the report against {vehicle}" — identical buttons need distinct accessible names. */
  readonly confirmNamed: string;
  readonly dismiss: string;
  readonly dismissNamed: string;
  readonly working: string;
}

const INITIAL: ReportDecisionState = {};

export function ReportDecisionForm({
  reportId,
  labels,
}: {
  reportId: string;
  labels: ReportDecisionLabels;
}) {
  const [state, formAction, pending] = useActionState(decideReport, INITIAL);

  return (
    <form action={formAction} className="flex flex-col items-end gap-xxs">
      <input type="hidden" name="reportId" value={reportId} />

      <div className="flex items-center justify-end gap-xxs">
        <Button
          type="submit"
          name="intent"
          value="confirm"
          size="compact"
          variant="danger"
          aria-label={labels.confirmNamed}
          disabled={pending}
          busy={pending}
          busyLabel={labels.working}
        >
          {labels.confirm}
        </Button>
        <Button
          type="submit"
          name="intent"
          value="dismiss"
          size="compact"
          variant="ghost"
          aria-label={labels.dismissNamed}
          disabled={pending}
        >
          {labels.dismiss}
        </Button>
      </div>

      {state.message ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}
    </form>
  );
}
