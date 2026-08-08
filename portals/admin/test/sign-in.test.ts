import { beforeEach, describe, expect, it, vi } from 'vitest';

import { localProblem, ProblemError } from '@/api/problem';
import type { AuthSession } from '@/api/types';
import { createAdminTranslator } from '@/i18n';

import { menuFor } from './support/urd';

/**
 * SCR-AP-001's server action: what an operator is told when sign-in fails, and
 * where they land when it succeeds.
 *
 * The deliverable's "failed-attempt lockout surfaced" is the interesting case.
 * AL-37 removed the second factor and kept the lock-out as one of its two
 * compensating controls; iam-svc answers `423 otp-locked` and puts the remaining
 * time on the problem as `retryAfterSeconds`. A portal that showed a bare "sign-in
 * failed" would leave an operator guessing for the rest of the window — and every
 * guess is another failure.
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
  getTranslator: async () => createAdminTranslator('en'),
}));

const { signIn, signOut } = await import('@/server/auth-actions');

const t = createAdminTranslator('en');

function form(fields: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(fields)) data.set(key, value);
  return data;
}

const CREDENTIALS = { email: 'nimali@mageride.lk', password: 'correct-horse-battery-staple' };

const ISSUED: AuthSession = {
  accessToken: 'access',
  refreshToken: 'refresh',
  expiresIn: 1800,
  user: { userId: '01JQ0', email: CREDENTIALS.email, firstName: 'Nimali', role: 'support_csr' },
};

beforeEach(() => {
  vi.clearAllMocks();
  getSession.mockResolvedValue({
    userId: '01JQ0',
    roles: ['support_csr'],
    permissions: [],
    menu: menuFor(['support_csr']),
    mfaRequired: false,
  });
});

async function expectRedirect(promise: Promise<unknown>): Promise<string> {
  await expect(promise).rejects.toBe(redirected);
  return (redirected as Error & { path?: string }).path!;
}

describe('validation happens before a credential is sent', () => {
  it('asks for the email rather than posting an empty one', async () => {
    const state = await signIn({}, form({ email: '  ', password: 'whatever' }));

    expect(state).toEqual({ message: t('admin.signIn.emailRequired'), field: 'email' });
    expect(signInWithPassword).not.toHaveBeenCalled();
  });

  it('asks for the password', async () => {
    const state = await signIn({}, form({ email: CREDENTIALS.email, password: '' }));

    expect(state).toEqual({ message: t('admin.signIn.passwordRequired'), field: 'password' });
    expect(signInWithPassword).not.toHaveBeenCalled();
  });
});

describe('the failed-attempt lock-out is surfaced (AL-37)', () => {
  it('tells the operator how long they are locked out for', async () => {
    signInWithPassword.mockRejectedValue(
      new ProblemError({
        ...localProblem('otp-locked', 423, '/v1/admin/auth/login'),
        retryAfterSeconds: 754,
      }),
    );

    const state = await signIn({}, form(CREDENTIALS));

    // 754s → "about 13 minutes". Rounded up, because being told to come back a
    // minute early is another failed attempt and another extension.
    expect(state.message).toBe(t('admin.error.accountLockedFor', { minutes: 13 }));
    expect(state.field).toBeUndefined();
  });

  it('never says "less than a minute", which reads as "try again now"', async () => {
    signInWithPassword.mockRejectedValue(
      new ProblemError({
        ...localProblem('otp-locked', 423, '/v1/admin/auth/login'),
        retryAfterSeconds: 3,
      }),
    );

    expect((await signIn({}, form(CREDENTIALS))).message).toBe(
      t('admin.error.accountLockedFor', { minutes: 1 }),
    );
  });

  it('falls back to the general lock-out message when no window is given', async () => {
    signInWithPassword.mockRejectedValue(
      new ProblemError(localProblem('otp-locked', 423, '/v1/admin/auth/login')),
    );

    expect((await signIn({}, form(CREDENTIALS))).message).toBe(t('admin.error.accountLocked'));
  });
});

describe('the other refusals', () => {
  it('says the same thing for an unknown email and a wrong password', async () => {
    // iam-svc answers 401 for both, deliberately, so the form is not an oracle
    // over who holds an internal account. The copy has to match, or it becomes
    // one.
    signInWithPassword.mockRejectedValue(
      new ProblemError(localProblem('unauthorized', 401, '/v1/admin/auth/login')),
    );

    const wrongPassword = await signIn({}, form(CREDENTIALS));
    const unknownEmail = await signIn({}, form({ ...CREDENTIALS, email: 'nobody@example.com' }));

    expect(wrongPassword.message).toBe(t('admin.error.invalidCredentials'));
    expect(unknownEmail.message).toBe(wrongPassword.message);
  });

  it('explains a 403 as a permissions answer, not a credential one', async () => {
    // A passenger or driver whose address exists, or a Google identity with no
    // portal account: real credentials, wrong surface (AL-02/AL-07).
    signInWithPassword.mockRejectedValue(
      new ProblemError(localProblem('forbidden', 403, '/v1/admin/auth/login')),
    );

    expect((await signIn({}, form(CREDENTIALS))).message).toBe(t('admin.error.forbidden'));
  });

  it('reports an unreachable platform as an outage', async () => {
    signInWithPassword.mockRejectedValue(
      new ProblemError(localProblem('dependency-unavailable', 503, '/v1/admin/auth/login')),
    );

    expect((await signIn({}, form(CREDENTIALS))).message).toBe(t('admin.error.serviceUnavailable'));
  });

  it('lets a non-problem error escape rather than swallowing it as a bad password', async () => {
    signInWithPassword.mockRejectedValue(new TypeError('boom'));
    await expect(signIn({}, form(CREDENTIALS))).rejects.toThrow(TypeError);
  });
});

describe('success completes with no second factor (AL-37, US-24.5)', () => {
  beforeEach(() => signInWithPassword.mockResolvedValue(ISSUED));

  it('establishes the session and redirects straight to the first permitted screen', async () => {
    const destination = await expectRedirect(signIn({}, form(CREDENTIALS)));

    expect(establishSession).toHaveBeenCalledWith(ISSUED);
    // No challenge state is returned and no second form is asked for: the next
    // thing after a correct password is the console.
    expect(destination).toBe('/dashboard');
  });

  it('sends a Verification Officer to their queue rather than to a dashboard', async () => {
    getSession.mockResolvedValue({
      userId: '01JQ0',
      roles: ['verification_officer'],
      permissions: [],
      menu: menuFor(['verification_officer']),
      mfaRequired: false,
    });

    expect(await expectRedirect(signIn({}, form(CREDENTIALS)))).toBe('/verification');
  });

  it('honours where the operator was going', async () => {
    const destination = await expectRedirect(
      signIn({}, form({ ...CREDENTIALS, next: '/finance/refunds' })),
    );

    expect(destination).toBe('/finance/refunds');
  });

  it('ignores an off-origin ?next=', async () => {
    const destination = await expectRedirect(
      signIn({}, form({ ...CREDENTIALS, next: 'https://evil.example/collect' })),
    );

    expect(destination).toBe('/dashboard');
  });

  it('lands somewhere that explains itself when the account opens nothing', async () => {
    getSession.mockResolvedValue({
      userId: '01JQ0',
      roles: [],
      permissions: [],
      menu: [],
      mfaRequired: false,
    });

    expect(await expectRedirect(signIn({}, form(CREDENTIALS)))).toBe('/');
  });
});

describe('signing out', () => {
  it('revokes the session and says so on the way back to the form', async () => {
    expect(await expectRedirect(signOut())).toBe('/login?signedOut=1');
    expect(revokeSession).toHaveBeenCalledOnce();
  });
});
