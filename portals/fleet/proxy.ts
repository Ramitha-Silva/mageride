import { NextResponse, type NextRequest } from 'next/server';

import { apiFetch } from '@/api/http';
import { ProblemError } from '@/api/problem';
import type {
  EffectivePermissions,
  FleetOrganisation,
  FleetSession,
  TokenPair,
} from '@/api/types';
import { cookiesAreSecure, refreshSkewSeconds, sessionCacheSeconds } from '@/config/env';
import { dispositionFor } from '@/server/access';
import {
  ACCESS_TOKEN_COOKIE,
  ACCESS_TOKEN_HEADER,
  EXPIRES_AT_COOKIE,
  PATHNAME_HEADER,
  REFRESH_TOKEN_COOKIE,
  SESSION_COOKIES,
  sessionCookieOptions,
} from '@/server/cookies';
import { normalisePath, PUBLIC_PATHS } from '@/server/routes';

/**
 * The Fleet Portal's request gate. Four jobs, in this order, on every request the
 * matcher below admits:
 *
 *  1. **Authentication.** No refresh cookie ⇒ the sign-in screen, with the URL the
 *     caller asked for kept so they land on it afterwards.
 *  2. **Token rotation.** D-29 gives the access JWT 30 minutes; this rotates it
 *     before it expires rather than after a request has already failed on it.
 *  3. **The two refusals.** A screen the caller's seat does not carry answers
 *     **403**; a screen their organisation is not approved for shows the
 *     pending-verification state. Both apply whether they typed the URL, followed
 *     a stale bookmark or clicked a link that should not have been drawn.
 *  4. **Stamping.** The pathname and the current bearer, for the render.
 *
 * ## Why the gate is here and not in a layout
 *
 * An App Router layout is **not re-rendered** when navigation moves between its
 * children — `/dashboard` → `/vehicles` re-renders the page and reuses the shell.
 * A guard in the shell would therefore run on the first page load of a session and
 * never again, which is precisely the case a route guard exists for. `proxy.ts`
 * runs on every request, including the RSC fetch a client-side navigation makes.
 *
 * ## What it evaluates, and why that takes two calls
 *
 * There is no `GET /v1/fleets/session`. The caller's seat comes from
 * `GET /v1/me/permissions` — iam-svc's own evaluation, from `iam.fleet_members` —
 * and the organisation's verification state from `GET /v1/fleets/{fleetId}`, the
 * only route that carries it. The pair is cached per bearer for
 * `FLEET_PORTAL_SESSION_CACHE_SECONDS`, so a navigation costs nothing and an
 * approval reaches the console within seconds.
 *
 * **It is still not authorization**, and AL-06 says so in as many words (US-21.1):
 * `FleetAccessFilter` re-reads the membership on every request and every endpoint
 * re-decides. What this stops is a console that offers a screen whose every
 * request would be refused — and a stale cache entry costs at most a screen that
 * renders and then 403s its own data.
 */

/** Cached evaluations, keyed by bearer. Bounded in size and in age. */
const sessionCache = new Map<string, { expiresAt: number; session: FleetSession | null }>();
const SESSION_CACHE_LIMIT = 500;

export async function proxy(request: NextRequest): Promise<NextResponse> {
  const path = normalisePath(request.nextUrl.pathname);

  const headers = new Headers(request.headers);
  headers.set(PATHNAME_HEADER, path);
  // Never honour an inbound copy: this header is how the proxy speaks to the
  // render, and a client that could set it would be choosing its own bearer.
  headers.delete(ACCESS_TOKEN_HEADER);

  if (PUBLIC_PATHS.includes(path)) {
    return NextResponse.next({ request: { headers } });
  }

  const refreshToken = request.cookies.get(REFRESH_TOKEN_COOKIE)?.value;
  if (!refreshToken) return toSignIn(request);

  let accessToken = request.cookies.get(ACCESS_TOKEN_COOKIE)?.value ?? null;
  let rotated: TokenPair | null = null;

  if (!accessToken || isExpiring(request.cookies.get(EXPIRES_AT_COOKIE)?.value)) {
    try {
      rotated = await rotate(refreshToken);
      accessToken = rotated.accessToken;
    } catch (error) {
      // A spent or revoked refresh token is a signed-out operator, not an error
      // page. Anything else — a 503 from iam-svc — is left to the render, which
      // still holds an access token that may have minutes left on it.
      if (error instanceof ProblemError && error.status === 401) return toSignIn(request, true);
      if (!accessToken) return toSignIn(request, true);
    }
  }

  if (!accessToken) return toSignIn(request, true);

  const session = await evaluate(accessToken);
  if (!session) return toSignIn(request, true);

  headers.set(ACCESS_TOKEN_HEADER, accessToken);

  const response = route(request, headers, session, path);
  if (rotated) writeRotatedTokens(response, rotated);
  return response;
}

/* ------------------------------------------------------------------------- */

/**
 * Turns the disposition into a response.
 *
 * Both refusals **rewrite** rather than redirect, so the operator stays on the URL
 * they asked for instead of watching their address bar be rewritten — and so a
 * link into a vehicle screen still works the moment the organisation is approved,
 * from the same bookmark.
 */
function route(
  request: NextRequest,
  headers: Headers,
  session: FleetSession,
  path: string,
): NextResponse {
  switch (dispositionFor(session, path)) {
    case 'render':
      return NextResponse.next({ request: { headers } });
    case 'pending':
      // `/pending` renders the verification state inside the chrome. Deliberately
      // a 200: this is a state of the organisation, not a refusal of the caller,
      // and the page's job is to say what happens next.
      return NextResponse.rewrite(new URL('/pending', request.url), { request: { headers } });
    case 'denied':
    case 'not-found':
      // A URL no screen claims is refused rather than 404'd, and that is the same
      // decision C104 made: deny-by-default cannot make an exception for "we could
      // not find a screen for this" without that becoming the way a future
      // unregistered route gets in ungated. `/denied` calls `forbidden()`, which
      // is what makes the status a real 403 rather than a 200 whose body says no.
      return NextResponse.rewrite(new URL('/denied', request.url), { request: { headers } });
  }
}

function isExpiring(expiresAt: string | undefined): boolean {
  const seconds = Number.parseInt(expiresAt ?? '', 10);
  if (!Number.isFinite(seconds)) return true;
  return seconds - Math.floor(Date.now() / 1000) <= refreshSkewSeconds();
}

/**
 * `POST /v1/auth/refresh`. The presented token is single-use and replaying a spent
 * one revokes the whole rotation family (D-29) — which is why this is the only
 * caller in the portal, and why it runs once per request, before anything reads a
 * token.
 */
async function rotate(refreshToken: string): Promise<TokenPair> {
  const { data } = await apiFetch<TokenPair>({
    path: '/v1/auth/refresh',
    method: 'POST',
    body: { refreshToken },
  });
  return data;
}

/**
 * The caller's seat and their organisation's state, from the two reads
 * `@/server/session` makes — repeated here rather than imported because
 * `getSession()` reads `next/headers`, which does not exist in the proxy.
 */
async function evaluate(accessToken: string): Promise<FleetSession | null> {
  const now = Date.now();
  const hit = sessionCache.get(accessToken);
  if (hit && hit.expiresAt > now) return hit.session;

  let session: FleetSession | null;
  try {
    const { data: effective } = await apiFetch<EffectivePermissions>({
      path: '/v1/me/permissions',
      accessToken,
    });

    session = {
      userId: effective.userId,
      roles: effective.roles,
      fleetRole: effective.fleetRole ?? null,
      fleetId: effective.fleetId ?? null,
      permissions: effective.permissions,
      organisation: effective.fleetId
        ? await readOrganisation(accessToken, effective.fleetId)
        : null,
    };
  } catch (error) {
    if (error instanceof ProblemError && (error.status === 401 || error.status === 403)) {
      session = null;
    } else {
      // The platform is unreachable. Refusing every route would turn a gateway
      // blip into "you have been signed out and your password will not help";
      // letting the request through means the render's own call answers 503 and
      // the operator is told what is actually wrong. Nothing is granted by this:
      // the endpoint behind the screen is the gate either way (US-21.1).
      throw error;
    }
  }

  if (sessionCache.size >= SESSION_CACHE_LIMIT) sessionCache.clear();
  sessionCache.set(accessToken, { expiresAt: now + sessionCacheSeconds() * 1000, session });
  return session;
}

async function readOrganisation(
  accessToken: string,
  fleetId: string,
): Promise<FleetOrganisation | null> {
  try {
    const { data } = await apiFetch<FleetOrganisation>({
      path: `/v1/fleets/${fleetId}`,
      accessToken,
    });
    return data;
  } catch (error) {
    // A membership whose organisation is gone reads as an account with none,
    // which is what it is. See `@/server/session`.
    if (error instanceof ProblemError && (error.status === 403 || error.status === 404)) return null;
    throw error;
  }
}

function writeRotatedTokens(response: NextResponse, tokens: TokenPair): void {
  const secure = cookiesAreSecure();
  const thirtyDays = 30 * 24 * 60 * 60;

  response.cookies.set(
    ACCESS_TOKEN_COOKIE,
    tokens.accessToken,
    sessionCookieOptions(secure, tokens.expiresIn),
  );
  response.cookies.set(
    REFRESH_TOKEN_COOKIE,
    tokens.refreshToken,
    sessionCookieOptions(secure, thirtyDays),
  );
  response.cookies.set(
    EXPIRES_AT_COOKIE,
    String(Math.floor(Date.now() / 1000) + tokens.expiresIn),
    sessionCookieOptions(secure, thirtyDays),
  );
}

/** Sends the caller to the sign-in screen, remembering where they were going. */
function toSignIn(request: NextRequest, clearCookies = false): NextResponse {
  const url = new URL('/login', request.url);

  const target = request.nextUrl.pathname + request.nextUrl.search;
  // Only a path, and only one this application serves. `?next=` is a redirect
  // instruction from an untrusted query string; accepting an absolute URL would
  // make the sign-in screen an open redirect onto anybody's phishing page.
  if (target.startsWith('/') && !target.startsWith('//') && target !== '/') {
    url.searchParams.set('next', target);
  }

  const response = NextResponse.redirect(url);
  if (clearCookies) {
    for (const name of SESSION_COOKIES) response.cookies.delete(name);
  }
  return response;
}

export const config = {
  /**
   * Everything except Next's own assets and the files served from `public/`.
   *
   * The proxy has nothing to say about a stylesheet, and running it on one would
   * put a session evaluation in front of every chunk a page loads. The extension
   * list is explicit rather than "any path containing a dot" so a screen whose
   * URL carries an identifier is never accidentally left ungated — and `.txt` is
   * on it because `robots.txt` exists precisely to be read by something that has
   * never signed in.
   */
  matcher: [
    '/((?!_next/static|_next/image|favicon.ico|.*\\.(?:svg|png|jpg|jpeg|gif|webp|ico|txt|xml|json|webmanifest|map)$).*)',
  ],
};
