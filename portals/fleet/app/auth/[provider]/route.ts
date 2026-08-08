import { randomUUID } from 'node:crypto';

import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import type { NextRequest } from 'next/server';

import { appleSignIn, cookiesAreSecure, googleSignIn } from '@/config/env';
import { OAUTH_STATE_COOKIE, oauthStateCookieOptions } from '@/server/cookies';
import { safeNextPath } from '@/server/next-path';
import { authorizeUrl, encodeState, isProviderId, type ProviderId } from '@/server/oauth';

/**
 * `GET /auth/google` and `GET /auth/apple` — the first leg of AL-07's two
 * federated arms.
 *
 * The portal only builds the authorize URL and remembers where the operator was
 * going. It never sees a client secret and never mints a session: the provider
 * signs an ID token, posts it back to `/auth/callback/{provider}`, and iam-svc —
 * which holds the JWKS trust and the audience allow-list — is what turns it into
 * a MageRide session. See `@/server/oauth` for why the flow is shaped this way.
 */

const TEN_MINUTES_SECONDS = 600;

export async function GET(
  request: NextRequest,
  context: { params: Promise<{ provider: string }> },
): Promise<Response> {
  const { provider } = await context.params;
  if (!isProviderId(provider)) redirect('/login');

  const config = configFor(provider);
  // Unconfigured is not an error page. The button that leads here is not rendered
  // when the client id is absent, so arriving here at all means a stale bookmark.
  if (!config) redirect('/login');

  const next = safeNextPath(request.nextUrl.searchParams.get('next'));
  const nonce = randomUUID();

  (await cookies()).set(
    OAUTH_STATE_COOKIE,
    encodeState({ nonce, ...(next ? { next } : {}) }),
    // Ten minutes: long enough to pick an account and type a password, short
    // enough that an abandoned attempt does not leave a usable state lying about.
    oauthStateCookieOptions(cookiesAreSecure(), TEN_MINUTES_SECONDS),
  );

  redirect(authorizeUrl(provider, config, nonce));
}

function configFor(provider: ProviderId) {
  return provider === 'google' ? googleSignIn() : appleSignIn();
}
