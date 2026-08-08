import { randomUUID } from 'node:crypto';

import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import type { NextRequest } from 'next/server';

import { cookiesAreSecure, googleSignIn } from '@/config/env';
import { OAUTH_STATE_COOKIE, sessionCookieOptions } from '@/server/cookies';
import { safeNextPath } from '@/server/next-path';

/**
 * `GET /auth/google` — the first leg of AL-07's Google Sign-In.
 *
 * The portal only builds the authorize URL and remembers where the operator was
 * going. It never sees a token: Google redirects back to `/auth/callback` with an
 * authorization **code**, and iam-svc — which holds the client secret and the
 * audience allow-list — is what exchanges it (`POST /v1/admin/auth/login`
 * `{googleAuthCode}`). A portal that exchanged the code itself would need the
 * secret in its own environment, and would be a second place a Google identity
 * could be turned into a MageRide session.
 */

const GOOGLE_AUTHORIZE = 'https://accounts.google.com/o/oauth2/v2/auth';

/** The state cookie: a nonce to match, and where to go afterwards. */
interface OAuthState {
  readonly nonce: string;
  readonly next?: string;
}

export async function GET(request: NextRequest): Promise<Response> {
  const google = googleSignIn();
  // Unconfigured is not an error page. The button that leads here is not rendered
  // when the client id is absent, so arriving here at all means a stale bookmark.
  if (!google) redirect('/login');

  const next = safeNextPath(request.nextUrl.searchParams.get('next'));
  const nonce = randomUUID();
  const state: OAuthState = { nonce, ...(next ? { next } : {}) };

  (await cookies()).set(
    OAUTH_STATE_COOKIE,
    Buffer.from(JSON.stringify(state), 'utf8').toString('base64url'),
    // Ten minutes: long enough to pick an account and type a password, short
    // enough that an abandoned attempt does not leave a usable state lying about.
    sessionCookieOptions(cookiesAreSecure(), 600),
  );

  const url = new URL(GOOGLE_AUTHORIZE);
  url.searchParams.set('client_id', google.clientId);
  url.searchParams.set('redirect_uri', google.redirectUri);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('scope', 'openid email profile');
  url.searchParams.set('state', nonce);
  // No refresh token is wanted: MageRide's own session is the D-29 pair, and a
  // Google refresh token would be a second, longer-lived credential to store.
  url.searchParams.set('access_type', 'online');
  // Internal staff share machines. Forcing the chooser stops a second operator
  // silently landing in the first one's account.
  url.searchParams.set('prompt', 'select_account');

  redirect(url.toString());
}
