'use server';

import { redirect } from 'next/navigation';

import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

import { landingPath } from './access';
import { safeNextPath } from './next-path';
import { establishSession, getSession, revokeSession, signInWithPassword } from './session';

/**
 * The shell's two server actions.
 *
 * **No MFA branch exists (AL-37, US-24.5).** A successful password check is the
 * whole sign-in: `establishSession` writes the cookies and the next line is a
 * redirect. There is no challenge state and no second form.
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

  if (!email) return { message: t('fleet.signIn.emailRequired'), field: 'email' };
  if (!password) return { message: t('fleet.signIn.passwordRequired'), field: 'password' };

  try {
    const auth = await signInWithPassword(email, password);
    await establishSession(auth);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;

    // AL-37 keeps the failed-attempt lock-out as the compensating control that
    // replaced the second factor, and iam-svc puts the remaining time on the
    // problem. Showing "try again in about 12 minutes" instead of a bare refusal
    // is what stops an operator burning the rest of the window guessing.
    const retryAfter = error.problem.retryAfterSeconds;
    if (error.code === 'otp-locked' && typeof retryAfter === 'number') {
      return {
        message: t('fleet.error.accountLockedFor', {
          minutes: Math.max(1, Math.ceil(retryAfter / 60)),
        }),
      };
    }

    // A 401 here is "these credentials do not match an account". iam-svc answers
    // the same way for an unknown email and a wrong password — deliberately, so
    // the form is not an oracle over who has an account — and the copy has to say
    // the same, or it becomes one.
    if (error.status === 401) {
      return { message: t('fleet.error.invalidCredentials'), field: 'password' };
    }

    // 403 is a real account that may not be *here*: `PortalSignInService`
    // refuses an address with neither an internal role nor a fleet standing,
    // because the passenger and driver apps are Phone-OTP only (AL-07). The copy
    // says how a fleet account comes to exist, because nothing on this screen can
    // create one — see the C111 handoff.
    if (error.status === 403) return { message: t('fleet.error.noFleetAccount') };

    return { message: t(error.messageKey) };
  }

  redirect(next ?? (await firstScreen()));
}

export async function signOut(): Promise<void> {
  await revokeSession();
  redirect('/login?signedOut=1');
}

/**
 * Where a freshly signed-in member lands: their first permitted screen, or the
 * root, which explains itself when there is none.
 */
async function firstScreen(): Promise<string> {
  const session = await getSession();
  return (session && landingPath(session)) ?? '/';
}
