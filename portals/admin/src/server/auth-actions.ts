'use server';

import { redirect } from 'next/navigation';

import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';
import { landingPath } from './access';
import { safeNextPath } from './next-path';
import { establishSession, getSession, revokeSession, signInWithPassword } from './session';

/**
 * SCR-AP-001's two server actions.
 *
 * **No MFA branch exists (AL-37, US-24.5).** A successful password check is the
 * whole sign-in: `establishSession` writes the cookies and the next line is a
 * redirect to the operator's first screen. There is no challenge state, no
 * `mfaRequired` check and no second form — D3' §0 and D7' §4.2 still describe one,
 * and AL-37 is later and wins (planner finding 3).
 */

export interface SignInState {
  /**
   * The failure, already translated. The action resolves it rather than returning
   * a key: the alternative is shipping all three locale tables to the browser so
   * a client component can look up one sentence.
   */
  readonly message?: string;
  /** Which field to mark, when the failure is about one. */
  readonly field?: 'email' | 'password';
}

export async function signIn(_state: SignInState, formData: FormData): Promise<SignInState> {
  const t = await getTranslator();

  const email = String(formData.get('email') ?? '').trim();
  const password = String(formData.get('password') ?? '');
  const next = safeNextPath(String(formData.get('next') ?? ''));

  if (!email) return { message: t('admin.signIn.emailRequired'), field: 'email' };
  if (!password) return { message: t('admin.signIn.passwordRequired'), field: 'password' };

  try {
    const auth = await signInWithPassword(email, password);
    await establishSession(auth);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;

    // AL-37 keeps the failed-attempt lock-out as one of its two compensating
    // controls, and iam-svc puts the remaining time on the problem. Showing "try
    // again in about 12 minutes" instead of a bare refusal is what stops an
    // operator burning the rest of the window guessing.
    const retryAfter = error.problem.retryAfterSeconds;
    if (error.code === 'otp-locked' && typeof retryAfter === 'number') {
      return {
        message: t('admin.error.accountLockedFor', { minutes: Math.max(1, Math.ceil(retryAfter / 60)) }),
      };
    }

    // A 401 here is "these credentials do not match an account". iam-svc answers
    // the same way for an unknown email and a wrong password — deliberately, so
    // the form is not an oracle over who has an internal account — and the copy
    // has to say the same, or it becomes one.
    if (error.status === 401) {
      return { message: t('admin.error.invalidCredentials'), field: 'password' };
    }

    // 403 is a real account that may not be here: a passenger or a driver whose
    // address exists, or an identity with no portal account at all (AL-02/AL-07).
    if (error.status === 403) return { message: t('admin.error.forbidden') };

    return { message: t(error.messageKey) };
  }

  redirect(next ?? (await firstScreen()));
}

export async function signOut(): Promise<void> {
  await revokeSession();
  redirect('/login?signedOut=1');
}

/**
 * Where a freshly signed-in operator lands: their first permitted screen, or the
 * root, which explains itself when there is none.
 */
async function firstScreen(): Promise<string> {
  const session = await getSession();
  return (session && landingPath(session)) ?? '/';
}
