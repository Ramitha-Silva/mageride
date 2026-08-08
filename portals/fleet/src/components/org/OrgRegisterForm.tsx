'use client';

import { useActionState } from 'react';

import { Button, Field, Input, Textarea } from '@mageride/ui';

import { registerOrganisation, type OrgActionState } from '@/server/org-actions';

/**
 * **SCR-FP-002 before there is an organisation** — the form that registers one
 * (`POST /v1/fleets`, US-13.A7).
 *
 * The wireframe draws this screen with an organisation already on it, because
 * that is the state it spends its life in. The state before that is real and is
 * exactly one operator's first five minutes: `PermissionEvaluator` describes it
 * as "a `fleet_owner` with no membership row", `./access` gives that session one
 * reachable screen, and this is what that screen shows.
 *
 * Every string arrives already translated, as a prop — the same reason
 * `SignInForm` does it: importing the translator here would ship all three locale
 * tables to the browser so one form could look up a dozen sentences.
 */

export interface OrgRegisterLabels {
  readonly heading: string;
  readonly body: string;
  readonly name: string;
  readonly registrationNo: string;
  readonly registrationHint: string;
  readonly contactPhone: string;
  readonly contactPhoneHint: string;
  readonly contactEmail: string;
  readonly address: string;
  readonly optional: string;
  readonly required: string;
  readonly gate: string;
  readonly submit: string;
  readonly submitting: string;
}

const INITIAL: OrgActionState = {};

export function OrgRegisterForm({ labels }: { labels: OrgRegisterLabels }) {
  const [state, formAction, pending] = useActionState(registerOrganisation, INITIAL);

  const errorFor = (field: OrgActionState['field']) =>
    state.field === field && state.message ? { error: state.message } : {};

  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
      <h2 className="text-subtitle font-semibold">{labels.heading}</h2>
      <p className="text-body-sm text-on-surface-variant">{labels.body}</p>

      <form action={formAction} className="flex flex-col gap-sm">
        <Field label={labels.name} required requiredLabel={labels.required} {...errorFor('name')}>
          <Input name="name" autoComplete="organization" required maxLength={200} />
        </Field>

        <div className="flex flex-col gap-sm md:flex-row">
          <Field
            label={labels.registrationNo}
            hint={labels.registrationHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('registrationNo')}
          >
            <Input name="registrationNo" required maxLength={64} />
          </Field>

          <Field
            label={labels.contactPhone}
            hint={labels.contactPhoneHint}
            required
            requiredLabel={labels.required}
            className="flex-1"
            {...errorFor('contactPhone')}
          >
            <Input name="contactPhone" type="tel" autoComplete="tel" required />
          </Field>
        </div>

        <Field
          label={labels.contactEmail}
          hint={labels.optional}
          {...errorFor('contactEmail')}
        >
          <Input name="contactEmail" type="email" autoComplete="email" autoCapitalize="none" />
        </Field>

        <Field label={labels.address} hint={labels.optional}>
          <Textarea name="address" maxLength={500} rows={2} />
        </Field>

        {state.message && !state.field ? (
          <p role="alert" className="text-body-sm text-error">
            {state.message}
          </p>
        ) : null}

        {/*
          US-13.A7, said before the form is submitted rather than after: an
          organisation is created PENDING and stays there until a Verification
          Officer approves it, and until then the console is read-only apart from
          these setup screens.
        */}
        <p className="rounded-md bg-surface-variant px-sm py-xs text-caption text-on-surface-variant">
          {labels.gate}
        </p>

        <Button type="submit" busy={pending} busyLabel={labels.submitting} className="self-start">
          {labels.submit}
        </Button>
      </form>
    </section>
  );
}
