import 'server-only';

import { randomUUID } from 'node:crypto';

import { cache } from 'react';
import { cookies, headers } from 'next/headers';

import { apiFetch } from '@/api/http';
import { ProblemError } from '@/api/problem';
import type {
  AuthSession,
  EffectivePermissions,
  FleetOrganisation,
  FleetSession,
  TokenPair,
  UserProfile,
} from '@/api/types';
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
 * **No credential is checked here and none ever will be.** AL-07 puts
 * Email+Password, Google and Apple on iam-svc (`/v1/auth/password`,
 * `/v1/auth/google`, `/v1/auth/apple`), and iam-svc owns the failed-attempt
 * lock-out that AL-37 kept in place of a second factor. A portal that verified
 * anything itself would be a second place a password could be checked and a
 * second place the lock-out could be forgotten.
 *
 * **And no MFA step exists (AL-37).** All three arms return a token pair directly;
 * there is no challenge screen and no branch waiting for one.
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
 * Who is asking, which organisation they belong to, what they may do in it, and
 * whether it has been approved — from **two** server-side reads.
 *
 * ## Why two, when the Admin Portal makes one
 *
 * `GET /v1/admin/session` exists and answers all of it for admin-bff. **fleet-svc
 * has no such route**, and nothing on any contract composes these two facts, so
 * the shell composes them:
 *
 *  - `GET /v1/me/permissions` (iam-svc) is the caller's evaluated row set *plus*
 *    `fleetId` and `fleetRole`, read from `iam.fleet_members` rather than from the
 *    token — which carries only the most privileged of a person's memberships.
 *    It is the same `IPermissionEvaluator` output every fleet-svc route gates on,
 *    which is what URD §2.2's "the UI is rendered from the same permission model
 *    the API enforces" means here.
 *  - `GET /v1/fleets/{fleetId}` (fleet-svc) is the organisation itself, and the
 *    only place `status` can be read. It is deliberately *not* gated on approval
 *    — "a PENDING org's owner needs to see that it is pending" (FleetEndpoints).
 *
 * The second read is skipped when the first says there is no membership, which is
 * the state a Fleet Owner is in before they have registered an organisation.
 *
 * `cache()` dedupes the pair across a render: the layout draws the nav from it,
 * the page checks its own route against it, and the data layer scopes every URL
 * with its `fleetId` — three consumers, one pair of calls, and no chance of two of
 * them disagreeing because one happened to fetch a moment later.
 *
 * `null` means "not signed in, or the session is no longer honoured". Deliberately
 * not an exception: the caller's answer to both is the same redirect.
 */
export const getSession = cache(async (): Promise<FleetSession | null> => {
  const token = await accessToken();
  if (!token) return null;

  let effective: EffectivePermissions;
  try {
    const { data } = await apiFetch<EffectivePermissions>({
      path: '/v1/me/permissions',
      accessToken: token,
    });
    effective = data;
  } catch (error) {
    if (error instanceof ProblemError && (error.status === 401 || error.status === 403)) {
      return null;
    }
    // A 503 from the gateway is not "signed out". Letting it propagate keeps a
    // platform outage looking like an outage instead of silently bouncing every
    // operator to the sign-in screen, where their password would not help.
    throw error;
  }

  const organisation = effective.fleetId ? await readOrganisation(token, effective.fleetId) : null;

  return {
    userId: effective.userId,
    roles: effective.roles,
    fleetRole: effective.fleetRole ?? null,
    fleetId: effective.fleetId ?? null,
    permissions: effective.permissions,
    organisation,
  };
});

/**
 * The organisation named by the caller's own membership.
 *
 * A 403 or 404 here is not an error page. `iam.fleet_members` and
 * `registry.fleets` are two tables in two bounded contexts, and a membership whose
 * organisation has been removed is a state the portal can find itself reading. The
 * session then looks like an account with no organisation — which is what it is —
 * and the shell offers the one screen that creates one.
 */
async function readOrganisation(token: string, fleetId: string): Promise<FleetOrganisation | null> {
  try {
    const { data } = await apiFetch<FleetOrganisation>({
      path: `/v1/fleets/${fleetId}`,
      accessToken: token,
    });
    return data;
  } catch (error) {
    if (error instanceof ProblemError && (error.status === 403 || error.status === 404)) {
      return null;
    }
    throw error;
  }
}

/* ---------------------------------------------------------------------------
 * Sign-in — AL-07's three arms
 * ------------------------------------------------------------------------ */

/** `POST /v1/auth/password` — Email + password (AL-07/AL-37). */
export async function signInWithPassword(email: string, password: string): Promise<AuthSession> {
  return portalSignIn('/v1/auth/password', { email, password });
}

/**
 * `POST /v1/auth/google` / `POST /v1/auth/apple` — a provider **ID token**.
 *
 * Both routes take `{idToken}` and verify it against the provider's JWKS on the
 * far side. The portal never holds a client secret and never mints anything: it
 * carries a token the provider signed from the browser to iam-svc, which is the
 * only place a Google or Apple identity becomes a MageRide session.
 */
export async function signInWithProvider(
  provider: 'google' | 'apple',
  idToken: string,
): Promise<AuthSession> {
  return portalSignIn(`/v1/auth/${provider}`, { idToken });
}

/**
 * The three arms differ only in their path and body.
 *
 * The `Idempotency-Key` is sent because `_shared.yaml` marks the parameter
 * `required: true` on all three routes. A sign-in is not a mutation this portal
 * would otherwise key, but a contract-required header is not the portal's to omit.
 */
async function portalSignIn(path: string, body: unknown): Promise<AuthSession> {
  const { data } = await apiFetch<AuthSession>({
    path,
    method: 'POST',
    body,
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
      await apiFetch({ path: '/v1/auth/logout', method: 'POST', accessToken: token });
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
