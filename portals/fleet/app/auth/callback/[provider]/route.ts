import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import type { NextRequest } from 'next/server';

import { ProblemError } from '@/api/problem';
import { appleSignIn, googleSignIn } from '@/config/env';
import { landingPath } from '@/server/access';
import { OAUTH_STATE_COOKIE } from '@/server/cookies';
import { safeNextPath } from '@/server/next-path';
import { decodeState, isProviderId, stateMatches, type ProviderId } from '@/server/oauth';
import { establishSession, getSession, signInWithProvider } from '@/server/session';

/**
 * `POST /auth/callback/google` and `POST /auth/callback/apple` — the second leg
 * of AL-07's federated arms, and the URLs registered on both the provider's
 * client and in `GOOGLE_OIDC_REDIRECT_URI` / `APPLE_OIDC_REDIRECT_URI`. They are
 * fixed by those registrations; renaming this route means changing all three
 * together.
 *
 * **It is a POST, and it has to be.** Both providers return the identity token in
 * `response_mode=form_post` — Apple requires it for any response type carrying an
 * `id_token`, and Google's implicit alternative is `fragment`, which never
 * reaches a server. That makes this a cross-site form submission, which is why
 * the state cookie is the one `SameSite=None` cookie on this portal
 * (`oauthStateCookieOptions`).
 *
 * **`state` is checked before anything else happens.** Without it, any page on
 * the internet could post its own `id_token` here and sign a fleet operator into
 * somebody else's Google account — the login-CSRF the parameter exists for. The
 * nonce is in an httpOnly cookie this origin set, so a forged submission cannot
 * produce a matching one.
 *
 * Every failure lands back on `/login?error={provider}` rather than on an error
 * page: whatever went wrong, the operator's next move is to sign in, and the
 * password arm is right there.
 */

export async function POST(
  request: NextRequest,
  context: { params: Promise<{ provider: string }> },
): Promise<Response> {
  const { provider } = await context.params;
  if (!isProviderId(provider)) redirect('/login');
  if (!configured(provider)) redirect('/login');

  const jar = await cookies();
  const stored = decodeState(jar.get(OAUTH_STATE_COOKIE)?.value);
  // Spent the moment it is read, whatever happens next. A state cookie that
  // survives a failed attempt is a state cookie a second attempt can replay.
  jar.delete(OAUTH_STATE_COOKIE);

  const form = await readForm(request);
  const idToken = form.get('id_token');
  const state = form.get('state');

  // The operator pressed Cancel on the provider's screen — not a failure to
  // report, just a return to where they were.
  if (form.get('error') === 'user_cancelled_authorize' || form.get('error') === 'access_denied') {
    redirect('/login');
  }

  if (!idToken || !stored || !stateMatches(state, stored.nonce)) {
    redirect(`/login?error=${provider}`);
  }

  try {
    const auth = await signInWithProvider(provider, idToken);
    await establishSession(auth);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // 403 is the common one and it is not a bug: `PortalSignInService` refuses an
    // identity with neither an internal role nor a fleet standing, because a
    // first sign-in never creates a fleet account (AL-03/AL-07). The sign-in
    // screen says how one comes to exist.
    redirect(`/login?error=${provider}`);
  }

  const next = safeNextPath(stored.next);
  if (next) redirect(next);

  const session = await getSession();
  redirect((session && landingPath(session)) ?? '/');
}

/**
 * A GET here is a stale bookmark or somebody pasting the redirect URI into a
 * browser — never the provider, which was told `form_post`. Sending them to the
 * sign-in screen is the only useful answer.
 */
export async function GET(): Promise<Response> {
  redirect('/login');
}

/**
 * The posted body, as fields.
 *
 * `request.formData()` and nothing else: the body is `application/x-www-form-urlencoded`
 * from a third party, so it is read once, into strings, and never parsed as JSON
 * or interpolated into anything. A non-form body throws, and the catch sends the
 * caller to the sign-in screen rather than to a 500.
 */
async function readForm(request: NextRequest): Promise<Map<string, string>> {
  const fields = new Map<string, string>();
  try {
    const data = await request.formData();
    for (const [key, value] of data.entries()) {
      if (typeof value === 'string') fields.set(key, value);
    }
  } catch {
    // Deliberately empty: an unreadable body has no `id_token` and no `state`,
    // which is exactly the case the check below refuses.
  }
  return fields;
}

function configured(provider: ProviderId): boolean {
  return (provider === 'google' ? googleSignIn() : appleSignIn()) !== null;
}
