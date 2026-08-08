'use client';

import { useActionState } from 'react';

import {
  Button,
  StatusPill,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  TableEmpty,
  type StatusTone,
} from '@mageride/ui';

import { revokeAssignment, type DriverActionState } from '@/server/driver-actions';

/**
 * **SCR-FP-005's assignment table** — the roster and the history in one list
 * (US-13.2, US-13.8).
 *
 * ## Revoked and expired rows stay
 *
 * "Assignment history retained per vehicle" is the sketch's own caption, and
 * `GET …/assignments` answers with the history rather than the open assignments.
 * A table that filtered to the active ones would lose the answer to "who was
 * driving that bus in June", which is the question an operator asks a fleet
 * console after an incident.
 *
 * ## Revoke is a form per row, not a handler
 *
 * Each row posts its own `assignmentId` through the server action, so the button
 * works before hydration and each row's pending state is its own. `useActionState`
 * on the table would make one press grey out every button in it.
 */

export interface AssignmentRow {
  readonly assignmentId: string;
  readonly driver: string;
  readonly driverSecondary: string | null;
  readonly vehicle: string;
  readonly since: string;
  readonly until: string;
  readonly status: string;
  readonly statusTone: StatusTone;
  /** Whether this row can still be revoked — an ended one cannot. */
  readonly revocable: boolean;
}

export interface AssignmentTableLabels {
  readonly heading: string;
  readonly caption: string;
  readonly driver: string;
  readonly vehicle: string;
  readonly since: string;
  readonly until: string;
  readonly status: string;
  readonly actions: string;
  readonly empty: string;
  readonly revoke: string;
  readonly revoking: string;
  readonly revokeNote: string;
  readonly history: string;
}

export function AssignmentTable({
  rows,
  canRevoke,
  labels,
}: {
  rows: readonly AssignmentRow[];
  canRevoke: boolean;
  labels: AssignmentTableLabels;
}) {
  return (
    <section className="flex flex-col gap-sm">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <Table caption={labels.caption}>
        <THead>
          <TR>
            <TH>{labels.driver}</TH>
            <TH>{labels.vehicle}</TH>
            <TH>{labels.since}</TH>
            <TH>{labels.until}</TH>
            <TH>{labels.status}</TH>
            <TH>
              <span className="sr-only">{labels.actions}</span>
            </TH>
          </TR>
        </THead>
        <TBody>
          {rows.length === 0 ? (
            <TableEmpty colSpan={6}>{labels.empty}</TableEmpty>
          ) : (
            rows.map((row) => (
              <TR key={row.assignmentId}>
                <TD>
                  <span className="font-semibold">{row.driver}</span>
                  {row.driverSecondary ? (
                    <span className="block text-caption text-on-surface-variant">
                      {row.driverSecondary}
                    </span>
                  ) : null}
                </TD>
                <TD className="whitespace-nowrap">{row.vehicle}</TD>
                <TD className="whitespace-nowrap">{row.since}</TD>
                <TD className="whitespace-nowrap">{row.until}</TD>
                <TD>
                  <StatusPill tone={row.statusTone}>{row.status}</StatusPill>
                </TD>
                <TD>
                  {canRevoke && row.revocable ? (
                    <RevokeButton
                      assignmentId={row.assignmentId}
                      label={labels.revoke}
                      busyLabel={labels.revoking}
                    />
                  ) : null}
                </TD>
              </TR>
            ))
          )}
        </TBody>
      </Table>

      <p className="text-caption text-on-surface-variant">{labels.history}</p>
      {canRevoke ? (
        <p className="text-caption text-on-surface-variant">{labels.revokeNote}</p>
      ) : null}
    </section>
  );
}

const INITIAL: DriverActionState = {};

function RevokeButton({
  assignmentId,
  label,
  busyLabel,
}: {
  assignmentId: string;
  label: string;
  busyLabel: string;
}) {
  const [state, formAction, pending] = useActionState(revokeAssignment, INITIAL);

  return (
    <form action={formAction} className="flex flex-col gap-xxs">
      <input type="hidden" name="assignmentId" value={assignmentId} />
      <Button type="submit" size="compact" variant="danger" busy={pending} busyLabel={busyLabel}>
        {label}
      </Button>
      {state.message ? (
        <span role="alert" className="text-caption text-error">
          {state.message}
        </span>
      ) : null}
    </form>
  );
}
