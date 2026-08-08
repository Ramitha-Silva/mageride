import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import type { NextRequest } from 'next/server';

import { ProblemError } from '@/api/problem';
import { googleSignIn } from '@/config/env';
import { landingPath } from '@/server/access';
import { OAUTH_STATE_COOKIE } from '@/server/cookies';
import { safeNextPath } from '@/server/next-path';
import { establishSession, getSession, signInWithGoogleCode } from '@/server/session';

/**
 * `GET /auth/callback` — the second leg of Google Sign-In, and the URL registered
 * as `Oidc__Google__RedirectUri` on both the Google client and iam-svc
 * (`infra/env/.env.app.example`). It is fixed by those registrations; renaming
 * this route means changing all three together.
 *
 * **`state` is checked before anything else happens.** Without it, any page on
 * the internet could send a browser here with its own `code` and sign a MageRide
 * operator into somebody else's Google account — the login-CSRF the parameter
 * exists for. The nonce is in an httpOnly cookie this origin set, so a forged
 * request cannot produce a matching one.
 *
 * Every failure lands back on `/login?error=google` rather than on an error page:
 * whatever went wrong, the operator's next move is to sign in, and the password
 * arm is right there.
 */
export async function GET(request: NextRequest): Promise<Response> {
  const google = googleSignIn();
  if (!google) redirect('/login');

  const jar = await cookies();
  const stored = readState(jar.get(OAUTH_STATE_COOKIE)?.value);
  jar.delete(OAUTH_STATE_COOKIE);

  const params = request.nextUrl.searchParams;
  const code = params.get('code');
  const state = params.get('state');

  // `error=access_denied` is the operator pressing Cancel on Google's screen —
  // not a failure to report, just a return to where they were.
  if (params.get('error') === 'access_denied') redirect('/login');

  if (!code || !state || !stored || state !== stored.nonce) redirect('/login?error=google');

  try {
    const auth = await signInWithGoogleCode(code, google.redirectUri);
    await establishSession(auth);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // 403 is the common one and it is not a bug: iam-svc refuses a Google
    // identity with no MageRide portal account, because internal roles are
    // provisioned by a Super Admin and a first sign-in never creates one (AL-06).
    redirect('/login?error=google');
  }

  const next = safeNextPath(stored.next);
  if (next) redirect(next);

  const session = await getSession();
  redirect((session && landingPath(session)) ?? '/');
}

function readState(value: string | undefined): { nonce: string; next?: string } | null {
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(Buffer.from(value, 'base64url').toString('utf8'));
    if (!parsed || typeof parsed !== 'object') return null;
    const { nonce, next } = parsed as { nonce?: unknown; next?: unknown };
    if (typeof nonce !== 'string' || !nonce) return null;
    return { nonce, ...(typeof next === 'string' ? { next } : {}) };
  } catch {
    return null;
  }
}
