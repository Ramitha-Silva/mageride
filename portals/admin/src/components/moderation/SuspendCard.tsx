'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Select, Textarea } from '@mageride/ui';

import { SUSPEND_SUBJECTS, type SuspendSubject } from '@/api/moderation';
import { suspendSubject, type SuspendState } from '@/server/moderation-actions';

/**
 * SCR-AP-004's **Suspend / ban** card (US-14.3).
 *
 * ## Two deviations from the sketch, both because the platform says so
 *
 *  - **"Driver / vehicle ID" is two controls, not one.** A driver and a vehicle
 *    are two routes (`POST /v1/admin/drivers/{id}/suspend` and
 *    `…/vehicles/{id}/suspend`) doing two different things — one ends a person's
 *    session and blocks new dispatch, the other takes a vehicle off the map — and
 *    a bare id says nothing about which it names. Guessing would mean sending the
 *    request twice, or sending it to the wrong service and calling the 404 an
 *    answer.
 *  - **There is no Duration.** The sketch offers "Temporary — 24h ▾"; the contract
 *    body is one field, admin-bff writes `dispatch_state = DISPATCH_SUSPENDED` /
 *    `is_blocked = true`, and nothing anywhere schedules a reinstatement. A
 *    dropdown promising one would be a control whose value is discarded — the
 *    worst kind, because the operator would go home believing the driver comes
 *    back tomorrow.
 *
 * So the card says what a suspension is instead, and both are recorded in the
 * C107 handoff.
 *
 * ## The reason is required in three places
 *
 * Here, in `suspendSubject`, and in admin-bff — whose own comment is the argument:
 * "a suspension with no recorded reason is one nobody can appeal and nobody can
 * explain, which is the half of D-35 the audit row cannot supply on its own." Only
 * the third is authorization; the two before it are about not asking.
 */

export interface SuspendCardLabels {
  readonly heading: string;
  readonly subject: string;
  readonly driver: string;
  readonly vehicle: string;
  readonly subjectId: string;
  readonly subjectIdHint: string;
  readonly reason: string;
  readonly reasonHint: string;
  readonly apply: string;
  readonly working: string;
  readonly noDuration: string;
  readonly audit: string;
  /** "Driver suspended." / "Vehicle suspended." followed by the row it was written to. */
  readonly suspendedDriver: string;
  readonly suspendedVehicle: string;
  readonly recordedDriver: string;
  readonly recordedVehicle: string;
}

const INITIAL: SuspendState = {};

export function SuspendCard({
  subject,
  subjectId,
  labels,
}: {
  /** Aimed by the queue row the operator came from, through the URL. */
  subject: SuspendSubject;
  subjectId: string;
  labels: SuspendCardLabels;
}) {
  const [state, formAction, pending] = useActionState(suspendSubject, INITIAL);

  return (
    <section
      id="suspend"
      className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
    >
      <h2 className="text-subtitle font-semibold text-on-surface">{labels.heading}</h2>

      <form action={formAction} className="flex flex-wrap items-end gap-sm">
        <Field label={labels.subject} className="w-[160px]">
          <Select name="subject" defaultValue={subject}>
            {SUSPEND_SUBJECTS.map((value) => (
              <option key={value} value={value}>
                {value === 'vehicle' ? labels.vehicle : labels.driver}
              </option>
            ))}
          </Select>
        </Field>

        <Field
          label={labels.subjectId}
          hint={labels.subjectIdHint}
          className="min-w-[280px] flex-1"
          {...(state.field === 'subjectId' && state.message ? { error: state.message } : {})}
        >
          <Input
            name="subjectId"
            defaultValue={subjectId}
            maxLength={36}
            autoCapitalize="none"
            spellCheck={false}
          />
        </Field>

        <Field
          label={labels.reason}
          hint={labels.reasonHint}
          className="min-w-[240px] flex-1"
          {...(state.field === 'reason' && state.message ? { error: state.message } : {})}
        >
          <Textarea name="reason" maxLength={1000} rows={2} />
        </Field>

        <Button
          type="submit"
          size="compact"
          variant="danger"
          disabled={pending}
          busy={pending}
          busyLabel={labels.working}
        >
          {labels.apply}
        </Button>
      </form>

      {state.message && !state.field ? (
        <p role="alert" className="text-body-sm text-error">
          {state.message}
        </p>
      ) : null}

      {state.suspended ? (
        <p role="status" className="text-body-sm text-on-surface">
          {state.suspended.subject === 'vehicle' ? labels.suspendedVehicle : labels.suspendedDriver}{' '}
          {state.suspended.subject === 'vehicle' ? labels.recordedVehicle : labels.recordedDriver}
        </p>
      ) : null}

      <p className="text-caption text-on-surface-variant">{labels.noDuration}</p>
      <p className="text-caption text-on-surface-variant">{labels.audit}</p>
    </section>
  );
}
