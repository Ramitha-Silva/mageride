'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Select } from '@mageride/ui';

import type { PayoutProfile } from '@/api/payout';
import { savePayoutProfile, type PayoutActionState } from '@/server/payout-actions';

/**
 * **SCR-FP-002a's left card** — "Bank account — receives Mode B subscription
 * payments" (AL-49, US-27.1).
 *
 * Four fields, in the wireframe's own two rows: Bank ▾ / Branch, then Account
 * number / Account holder name.
 *
 * ## The two sentences under the form are the screen
 *
 * "Account-holder name must match the org / owner-KYC name" is a *requirement on
 * the operator*, not a validation this portal can run — only the Verification
 * Officer holds the KYC record, and a portal that compared the field against the
 * organisation's own name would refuse a sole proprietor's perfectly correct
 * personal account. It is stated on the field.
 *
 * "Editing any field re-triggers verification" is BR-31.1, and while the profile
 * is **verified** it is a consequence worth warning about *before* the press:
 * fleet-svc inserts a new pending version and leaves the incumbent verified, so
 * nothing an owner is collecting today is redirected — but Paid classification
 * and the pay sheet keep pointing at the old account until an officer approves
 * the new one. An owner who did not know that would read the chip flipping back
 * to Pending as something having gone wrong.
 */

export interface PayoutFormLabels {
  readonly heading: string;
  readonly bank: string;
  readonly bankPlaceholder: string;
  readonly branch: string;
  readonly accountNo: string;
  readonly accountHolderName: string;
  readonly holderHint: string;
  readonly required: string;
  readonly editWarning: string;
  readonly editVerifiedWarning: string;
  readonly submit: string;
  readonly submitting: string;
  readonly saved: string;
}

const INITIAL: PayoutActionState = {};

export function PayoutProfileForm({
  profile,
  banks,
  labels,
}: {
  /** The version being edited, or null when the organisation has submitted none. */
  profile: PayoutProfile | null;
  readonly banks: readonly string[];
  labels: PayoutFormLabels;
}) {
  const [state, formAction, pending] = useActionState(savePayoutProfile, INITIAL);
  const verified = profile?.status === 'verified';

  // `bank` is free text on the wire and this list is local, so a stored value the
  // list does not carry is possible — an older row, or a bank added since. It is
  // kept as an option rather than dropped: a select that silently fell back to
  // the placeholder would let an owner correct their branch name and change
  // their bank without touching the field.
  const options =
    profile?.bank && !banks.includes(profile.bank) ? [profile.bank, ...banks] : banks;

  const errorFor = (field: PayoutActionState['field']) =>
    state.field === field && state.message ? { error: state.message } : {};

  return (
    <section className="flex flex-1 flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>

      <form action={formAction} className="flex flex-col gap-sm">
        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.bank}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('bank')}
          >
            {/*
              A dropdown, as the wireframe draws it. `bank` is free text on the
              wire, and a bank name typed three ways is three accounts to an
              officer reading a statement. The list is local and should not be —
              see `@/api/payout`.
            */}
            <Select name="bank" defaultValue={profile?.bank ?? ''} required>
              <option value="" disabled>
                {labels.bankPlaceholder}
              </option>
              {options.map((bank) => (
                <option key={bank} value={bank}>
                  {bank}
                </option>
              ))}
            </Select>
          </Field>

          <Field
            label={labels.branch}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('branch')}
          >
            <Input name="branch" defaultValue={profile?.branch ?? ''} required maxLength={120} />
          </Field>
        </div>

        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.accountNo}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('accountNo')}
          >
            <Input
              name="accountNo"
              inputMode="numeric"
              autoComplete="off"
              defaultValue={profile?.accountNo ?? ''}
              required
              maxLength={40}
            />
          </Field>

          <Field
            label={labels.accountHolderName}
            hint={labels.holderHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('accountHolderName')}
          >
            <Input
              name="accountHolderName"
              defaultValue={profile?.accountHolderName ?? ''}
              required
              maxLength={200}
            />
          </Field>
        </div>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {state.saved ? (
          <p role="status" className="text-body-sm text-success">
            {labels.saved}
          </p>
        ) : null}

        <p
          className={
            verified
              ? 'rounded-md border border-warning/40 bg-warning/10 px-sm py-xs text-caption text-on-surface'
              : 'text-caption text-on-surface-variant'
          }
        >
          {verified ? labels.editVerifiedWarning : labels.editWarning}
        </p>

        <Button type="submit" busy={pending} busyLabel={labels.submitting} className="self-start">
          {labels.submit}
        </Button>
      </form>
    </section>
  );
}
