'use client';

import { useActionState } from 'react';

import { Button, Field, Input } from '@mageride/ui';

import { signIn, type SignInState } from '@/server/auth-actions';

/**
 * The credential form on SCR-FP-001's **Sign in** tab.
 *
 * Every string arrives already translated, as a prop. The alternative — importing
 * the translator into a client component — would ship all three locale tables to
 * the browser so that one form could look up eight sentences, on the one page a
 * signed-out person can reach.
 *
 * **There is no second step after this one (AL-37).** The action either returns a
 * message or redirects; a challenge screen has nowhere to appear.
 */

export interface SignInLabels {
  readonly email: string;
  readonly password: string;
  readonly submit: string;
  readonly submitting: string;
}

const INITIAL: SignInState = {};

export function SignInForm({ labels, next }: { labels: SignInLabels; next?: string }) {
  const [state, formAction, pending] = useActionState(signIn, INITIAL);

  return (
    <form action={formAction} className="flex flex-col gap-md">
      {next ? <input type="hidden" name="next" value={next} /> : null}

      <Field
        label={labels.email}
        {...(state.field === 'email' && state.message ? { error: state.message } : {})}
      >
        <Input
          name="email"
          type="email"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          required
        />
      </Field>

      <Field
        label={labels.password}
        {...(state.field === 'password' && state.message ? { error: state.message } : {})}
      >
        <Input name="password" type="password" autoComplete="current-password" required />
      </Field>

      {state.message && !state.field ? (
        <p role="alert" className="text-body-sm text-error">
          {state.message}
        </p>
      ) : null}

      <Button type="submit" busy={pending} busyLabel={labels.submitting} className="w-full">
        {labels.submit}
      </Button>
    </form>
  );
}
