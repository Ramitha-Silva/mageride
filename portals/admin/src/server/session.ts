import 'server-only';

import { randomUUID } from 'node:crypto';

import { cache } from 'react';
import { cookies, headers } from 'next/headers';

import { apiFetch } from '@/api/http';
import { ProblemError } from '@/api/problem';
import type { AdminSession, AuthSession, TokenPair, UserProfile } from '@/api/types';
import { cookiesAreSecure } from '@/config/env';

import {
  ACCESS_TOKEN_COOKIE,
  ACCESS_TOKEN_HEADER,
  EXPIRES_AT_COOKIE,
  IDENTITY_COOKIE,
  REFRESH_TOKEN_COOKIE,
  SESSION_COOKIES,
  decodeIdentity,
  encodeIdentity,
  sessionCookieOptions,
  type DisplayIdentity,
} from './cookies';

/**
 * Sign-in, sign-out, token rotation and "who is asking" — the whole session half
 * of the shell.
 *
 * **No credential is checked here and none ever will be.** AL-07 puts password and
 * Google on iam-svc's `POST /v1/admin/auth/login`, and iam-svc owns the
 * failed-attempt lock-out and the optional IP allow-list with them (AL-37's two
 * compensating controls). A portal that verified anything itself would be a second
 * place a password could be checked and a second place the lock-out could be
 * forgotten — the same argument admin-bff's own file makes for not hosting the
 * route.
 *
 * **And no MFA step exists (AL-37).** There is no challenge screen, no second
 * factor, no branch on `mfaRequired` — sign-in completes straight to the
 * dashboard. `mfaRequired` is asserted to be false in the test suite rather than
 * acted on, because a `if (session.mfaRequired)` here would be a code path nobody
 * could ever reach and everybody would later believe in.
 */

/** How long the refresh cookie lives. D-29 gives the opaque refresh token 30 days. */
const REFRESH_TOKEN_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

/**
 * The bearer for this request: the freshly rotated token the proxy stamped, or the
 * cookie when nothing was rotated. In that order, always — see
 * {@link ACCESS_TOKEN_HEADER}.
 */
export async function accessToken(): Promise<string | null> {
  const rotated = (await headers()).get(ACCESS_TOKEN_HEADER);
  if (rotated) return rotated;

  return (await cookies()).get(ACCESS_TOKEN_COOKIE)?.value ?? null;
}

/** The caller's own name/email/language, for the chrome. Never authorization input. */
export async function displayIdentity(): Promise<DisplayIdentity | null> {
  return decodeIdentity((await cookies()).get(IDENTITY_COOKIE)?.value);
}

/**
 * `GET /v1/admin/session` — identity, the caller's row of URD §2.3, and the
 * role-scoped menu, from one server-side evaluation (URD §2.2).
 *
 * `cache()` dedupes it across the render: the layout draws the nav from it, the
 * page checks its own route against it, and a `<UserMenu>` names the roles — three
 * consumers, one call, and no chance of two of them disagreeing because one
 * happened to fetch a moment later.
 *
 * `null` means "not signed in, or the session is no longer honoured". It is
 * deliberately not an exception: the caller's answer to both is the same redirect,
 * and a thrown error would have to be caught in a layout to produce it.
 */
export const getSession = cache(async (): Promise<AdminSession | null> => {
  const token = await accessToken();
  if (!token) return null;

  try {
    const { data } = await apiFetch<AdminSession>({
      path: '/v1/admin/session',
      accessToken: token,
    });
    return data;
  } catch (error) {
    if (error instanceof ProblemError && (error.status === 401 || error.status === 403)) {
      return null;
    }
    // A 503 from the gateway is not "signed out". Letting it propagate keeps a
    // platform outage looking like an outage instead of silently bouncing every
    // operator to the sign-in screen, where their password would not help.
    throw error;
  }
});

/* ---------------------------------------------------------------------------
 * Sign-in
 * ------------------------------------------------------------------------ */

/** `POST /v1/admin/auth/login` — the password arm (AL-07/AL-37). */
export async function signInWithPassword(email: string, password: string): Promise<AuthSession> {
  const { data } = await apiFetch<AuthSession>({
    path: '/v1/admin/auth/login',
    method: 'POST',
    body: { email, password },
    // The gateway refuses every POST mutation without one — 400
    // `idempotency-key-required`, whose code is in no MESSAGE_KEYS entry, so it
    // surfaced as `admin.error.unexpected` and sign-in was impossible. These four
    // calls reach `apiFetch` directly and so bypass `api/client.ts`, which mints a
    // key for every mutation that goes through it; the fleet portal's
    // `portalSignIn` sets one for the same reason.
    idempotencyKey: randomUUID(),
  });
  return data;
}

/** `POST /v1/admin/auth/login` — the Google authorization-code arm (AL-07/AL-37). */
export async function signInWithGoogleCode(
  googleAuthCode: string,
  redirectUri: string,
): Promise<AuthSession> {
  const { data } = await apiFetch<AuthSession>({
    path: '/v1/admin/auth/login',
    method: 'POST',
    // The contract's body is a `oneOf`: both arms or neither is a 400, so the
    // password fields are absent rather than empty.
    body: { googleAuthCode, redirectUri },
    idempotencyKey: randomUUID(),
  });
  return data;
}

/**
 * Writes the issued session to cookies. Callable only from a server action or a
 * route handler — Next refuses a cookie write during a render, which is the
 * framework saying the same thing as "a GET must not establish a session".
 */
export async function establishSession(auth: AuthSession): Promise<void> {
  const jar = await cookies();
  const secure = cookiesAreSecure();
  const now = Math.floor(Date.now() / 1000);

  jar.set(ACCESS_TOKEN_COOKIE, auth.accessToken, sessionCookieOptions(secure, auth.expiresIn));
  jar.set(
    REFRESH_TOKEN_COOKIE,
    auth.refreshToken,
    sessionCookieOptions(secure, REFRESH_TOKEN_MAX_AGE_SECONDS),
  );
  jar.set(
    EXPIRES_AT_COOKIE,
    String(now + auth.expiresIn),
    sessionCookieOptions(secure, REFRESH_TOKEN_MAX_AGE_SECONDS),
  );
  jar.set(
    IDENTITY_COOKIE,
    encodeIdentity(identityOf(auth.user)),
    sessionCookieOptions(secure, REFRESH_TOKEN_MAX_AGE_SECONDS),
  );
}

function identityOf(user: UserProfile): DisplayIdentity {
  return {
    ...(user.firstName ? { name: user.firstName } : {}),
    ...(user.email ? { email: user.email } : {}),
    ...(user.language ? { language: user.language } : {}),
  };
}

/* ---------------------------------------------------------------------------
 * Rotation and sign-out
 * ------------------------------------------------------------------------ */

/**
 * `POST /v1/auth/refresh` — rotates the pair. The presented token is single-use and
 * replaying a spent one revokes the whole family (D-29), so this is called from
 * exactly one place: the proxy, once per request, before anything reads the token.
 */
export async function rotateTokens(refreshToken: string): Promise<TokenPair> {
  const { data } = await apiFetch<TokenPair>({
    path: '/v1/auth/refresh',
    method: 'POST',
    body: { refreshToken },
    idempotencyKey: randomUUID(),
  });
  return data;
}

/**
 * `POST /v1/auth/logout` — revokes `iam.sessions` + `refresh:{jti}` for this
 * browser only (US-1.7).
 *
 * A failure is swallowed on purpose. The operator pressed Sign out; the cookies go
 * either way, and leaving them in place because iam-svc was redeploying would
 * leave somebody signed in at a shared desk.
 */
export async function revokeSession(): Promise<void> {
  const token = await accessToken();
  if (token) {
    try {
      await apiFetch({
        path: '/v1/auth/logout',
        method: 'POST',
        accessToken: token,
        idempotencyKey: randomUUID(),
      });
    } catch {
      // Deliberately ignored — see above.
    }
  }

  await clearSession();
}

/** Drops every session cookie. */
export async function clearSession(): Promise<void> {
  const jar = await cookies();
  for (const name of SESSION_COOKIES) jar.delete(name);
}
