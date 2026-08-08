import { beforeEach, describe, expect, it, vi } from 'vitest';

import { localProblem, ProblemError } from '@/api/problem';
import type { AuthSession } from '@/api/types';
import { createFleetTranslator } from '@/i18n';

import { sessionFor } from './support/fleet';

/**
 * The credential arm of sign-in: what a fleet operator is told when it fails, and
 * where they land when it succeeds.
 *
 * Two cases carry the interesting decisions. **The lock-out** (AL-37's
 * compensating control) answers `423 otp-locked` with `retryAfterSeconds`, and a
 * portal that showed a bare "sign-in failed" would leave somebody guessing for the
 * rest of the window — every guess another failure. **The 403** is the one this
 * portal cannot fix from the sign-in screen: `PortalSignInService` refuses an
 * address with no fleet standing, and no route on any contract creates one, so the
 * copy has to say how an account comes to exist rather than implying a retry.
 */

const redirected = new Error('NEXT_REDIRECT');

const redirect = vi.fn((path: string) => {
  (redirected as Error & { path?: string }).path = path;
  throw redirected;
});

const signInWithPassword = vi.fn<(email: string, password: string) => Promise<AuthSession>>();
const establishSession = vi.fn();
const revokeSession = vi.fn();
const getSession = vi.fn();

vi.mock('next/navigation', () => ({ redirect: (path: string) => redirect(path) }));

vi.mock('@/server/session', () => ({
  signInWithPassword: (email: string, password: string) => signInWithPassword(email, password),
  establishSession: (auth: AuthSession) => establishSession(auth),
  revokeSession: () => revokeSession(),
  getSession: () => getSession(),
}));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
}));

const { signIn, signOut } = await import('@/server/auth-actions');

const t = createFleetTranslator('en');

function form(fields: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(fields)) data.set(key, value);
  return data;
}

const CREDENTIALS = { email: 'ops@lankatransit.lk', password: 'correct-horse-battery-staple' };

const ISSUED: AuthSession = {
  accessToken: 'access',
  refreshToken: 'refresh',
  expiresIn: 1800,
  user: {
    userId: '01JQ0',
    email: CREDENTIALS.email,
    firstName: 'Nimali',
    role: 'fleet_owner',
    fleetRole: 'owner',
  },
};

beforeEach(() => {
  vi.clearAllMocks();
  getSession.mockResolvedValue(sessionFor('owner'));
});

async function expectRedirect(promise: Promise<unknown>): Promise<string> {
  await expect(promise).rejects.toBe(redirected);
  return (redirected as Error & { path?: string }).path!;
}

describe('validation happens before a credential is sent', () => {
  it('asks for the email rather than posting an empty one', async () => {
    const state = await signIn({}, form({ email: '  ', password: 'whatever' }));

    expect(state).toEqual({ message: t('fleet.signIn.emailRequired'), field: 'email' });
    expect(signInWithPassword).not.toHaveBeenCalled();
  });

  it('asks for the password', async () => {
    const state = await signIn({}, form({ email: CREDENTIALS.email, password: '' }));

    expect(state).toEqual({ message: t('fleet.signIn.passwordRequired'), field: 'password' });
    expect(signInWithPassword).not.toHaveBeenCalled();
  });
});

describe('a successful sign-in', () => {
  it('establishes the session and lands on the caller’s own first screen', async () => {
    signInWithPassword.mockResolvedValue(ISSUED);

    const path = await expectRedirect(signIn({}, form(CREDENTIALS)));

    expect(establishSession).toHaveBeenCalledWith(ISSUED);
    expect(path).toBe('/dashboard');
  });

  it('sends a pending organisation’s owner to its setup screen instead', async () => {
    signInWithPassword.mockResolvedValue(ISSUED);
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));

    expect(await expectRedirect(signIn({}, form(CREDENTIALS)))).toBe('/org/setup');
  });

  it('honours a safe ?next= and ignores an absolute one', async () => {
    signInWithPassword.mockResolvedValue(ISSUED);

    expect(await expectRedirect(signIn({}, form({ ...CREDENTIALS, next: '/vehicles' })))).toBe(
      '/vehicles',
    );

    expect(
      await expectRedirect(signIn({}, form({ ...CREDENTIALS, next: '//evil.example' }))),
    ).toBe('/dashboard');
  });

  it('never reaches a second factor — there is no branch for one (AL-37)', async () => {
    signInWithPassword.mockResolvedValue(ISSUED);

    const path = await expectRedirect(signIn({}, form(CREDENTIALS)));
    expect(path).not.toContain('mfa');
    expect(path).not.toContain('challenge');
  });
});

describe('a failed sign-in', () => {
  function refuse(code: string, status: number, extra: Record<string, unknown> = {}) {
    signInWithPassword.mockRejectedValue(
      new ProblemError({ ...localProblem(code, status, '/v1/auth/password'), ...extra }),
    );
  }

  it('says the same thing for an unknown email and a wrong password', async () => {
    refuse('auth-not-found', 401);

    const state = await signIn({}, form(CREDENTIALS));
    expect(state).toEqual({ message: t('fleet.error.invalidCredentials'), field: 'password' });
  });

  it('surfaces the lock-out with the time left on it', async () => {
    refuse('otp-locked', 423, { retryAfterSeconds: 700 });

    const state = await signIn({}, form(CREDENTIALS));
    expect(state.message).toBe(t('fleet.error.accountLockedFor', { minutes: 12 }));
  });

  it('rounds a sub-minute lock-out up rather than saying zero minutes', async () => {
    refuse('otp-locked', 423, { retryAfterSeconds: 20 });

    const state = await signIn({}, form(CREDENTIALS));
    expect(state.message).toBe(t('fleet.error.accountLockedFor', { minutes: 1 }));
  });

  it('falls back to the generic lock-out sentence when no time is given', async () => {
    refuse('otp-locked', 423);

    const state = await signIn({}, form(CREDENTIALS));
    expect(state.message).toBe(t('fleet.error.accountLocked'));
  });

  it('explains a 403 as an account that is not a fleet account', async () => {
    refuse('forbidden', 403);

    const state = await signIn({}, form(CREDENTIALS));
    expect(state.message).toBe(t('fleet.error.noFleetAccount'));
  });

  it('renders an unexpected refusal from its code, never from problem.title', async () => {
    signInWithPassword.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/rate-limited',
        title: 'Too Many Requests',
        status: 429,
      }),
    );

    const state = await signIn({}, form(CREDENTIALS));
    expect(state.message).toBe(t('fleet.error.rateLimited'));
    expect(state.message).not.toContain('Too Many Requests');
  });

  it('does not swallow a failure that is not a problem at all', async () => {
    signInWithPassword.mockRejectedValue(new TypeError('boom'));

    await expect(signIn({}, form(CREDENTIALS))).rejects.toThrow('boom');
  });
});

describe('sign-out', () => {
  it('revokes the session and returns to the sign-in screen saying so', async () => {
    const path = await expectRedirect(signOut());

    expect(revokeSession).toHaveBeenCalledOnce();
    expect(path).toBe('/login?signedOut=1');
  });
});
