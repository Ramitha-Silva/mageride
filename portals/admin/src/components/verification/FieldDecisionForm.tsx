'use client';

import { useActionState, useState } from 'react';

import { Button, Input } from '@mageride/ui';

import { decideField, type FieldDecisionState } from '@/server/verification-actions';

/**
 * The **Confirm / Edit & confirm** pair one flagged field carries (D2 §SCR-AP-003,
 * US-2.4a/2.10a).
 *
 * ## Why Edit is a state and not a second control
 *
 * A row cannot hold a form that spans two cells — `<form>` is not a child a
 * `<tr>` admits — so the alternative to revealing the box on demand is a text
 * input on every pending row, permanently. That reads as "type here" on six rows
 * where the officer's answer is usually "the scan was right", and it makes the
 * two buttons ambiguous: an operator who types into the box and then presses
 * Confirm has every reason to expect their correction saved, and Confirm is
 * precisely the request that carries no value.
 *
 * So Confirm and Edit are the wireframe's two buttons; pressing Edit swaps the
 * cell for the box, the corrected value, and Cancel. **The two requests are then
 * unambiguous**, which is what the platform needs them to be: a bare confirm
 * leaves the extraction as evidence, and an edited value becomes `manual` with no
 * confidence, because a score invented for a value a person typed would read as
 * the model's agreement.
 *
 * Every string arrives translated, as a prop — `SignInForm`'s rule, for
 * `SignInForm`'s reason.
 */

export interface FieldDecisionLabels {
  readonly confirm: string;
  /** "Confirm {field}" — six identical buttons need six different accessible names. */
  readonly confirmNamed: string;
  readonly edit: string;
  readonly editNamed: string;
  readonly editConfirm: string;
  readonly cancel: string;
  readonly correctedValue: string;
  readonly working: string;
}

const INITIAL: FieldDecisionState = {};

export function FieldDecisionForm({
  subjectId,
  subjectType,
  fieldKey,
  value,
  returnTo,
  labels,
}: {
  subjectId: string;
  subjectType: string;
  fieldKey: string;
  /** The extracted value, offered as the starting point for a correction. */
  value: string;
  returnTo: string;
  labels: FieldDecisionLabels;
}) {
  const [editing, setEditing] = useState(false);
  const [state, formAction, pending] = useActionState(decideField, INITIAL);

  return (
    <form action={formAction} className="flex flex-col items-end gap-xxs">
      <input type="hidden" name="subjectId" value={subjectId} />
      <input type="hidden" name="subjectType" value={subjectType} />
      <input type="hidden" name="fieldKey" value={fieldKey} />
      <input type="hidden" name="returnTo" value={returnTo} />

      {editing ? (
        <div className="flex flex-wrap items-center justify-end gap-xxs">
          <Input
            name="value"
            defaultValue={value}
            aria-label={labels.correctedValue}
            maxLength={500}
            className="w-[180px]"
            required
          />
          <Button
            type="submit"
            name="intent"
            value="edit"
            size="compact"
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.editConfirm}
          </Button>
          <Button type="button" size="compact" variant="ghost" onClick={() => setEditing(false)}>
            {labels.cancel}
          </Button>
        </div>
      ) : (
        <div className="flex items-center justify-end gap-xxs">
          <Button
            type="submit"
            name="intent"
            value="confirm"
            size="compact"
            aria-label={labels.confirmNamed}
            busy={pending}
            busyLabel={labels.working}
          >
            {labels.confirm}
          </Button>
          <Button
            type="button"
            size="compact"
            variant="ghost"
            aria-label={labels.editNamed}
            onClick={() => setEditing(true)}
          >
            {labels.edit}
          </Button>
        </div>
      )}

      {state.message ? (
        <p role="alert" className="text-caption text-error">
          {state.message}
        </p>
      ) : null}
    </form>
  );
}
