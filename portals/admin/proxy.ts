import { NextResponse, type NextRequest } from 'next/server';

import { apiFetch } from '@/api/http';
import { ProblemError } from '@/api/problem';
import type { AdminSession, TokenPair } from '@/api/types';
import { cookiesAreSecure, refreshSkewSeconds, sessionCacheSeconds } from '@/config/env';
import { isReachable } from '@/server/access';
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
 * The Admin Portal's request gate. Four jobs, in this order, on every request the
 * matcher below admits:
 *
 *  1. **Authentication.** No refresh cookie ⇒ the sign-in screen, with the URL the
 *     caller asked for kept so they land on it afterwards.
 *  2. **Token rotation.** D-29 gives the access JWT 30 minutes; this rotates it
 *     before it expires rather than after a request has already failed on it.
 *  3. **Deny-by-default (AL-06).** A screen the caller's URD §2.3 row does not
 *     permit answers **403**, whether they typed the URL, followed a stale
 *     bookmark or clicked a link that should not have been drawn.
 *  4. **Stamping.** The pathname and the current bearer, for the render.
 *
 * ## Why the gate is here and not in a layout
 *
 * An App Router layout is **not re-rendered** when navigation moves between its
 * children — `/dashboard` → `/finance/refunds` re-renders the page and reuses the
 * shell. A guard in the shell would therefore run on the first page load of a
 * session and never again, which is precisely the case a route guard exists for.
 * `proxy.ts` runs on every request, including the RSC fetch a client-side
 * navigation makes, so there is no navigation it does not see.
 *
 * ## Why it asks admin-bff rather than deciding
 *
 * The refusal has to agree with the API's own, and the only way to be sure of that
 * is to use the API's own evaluation: `GET /v1/admin/session` returns the menu
 * admin-bff filtered through the same `IPermissionEvaluator` its endpoints gate
 * on. Transcribing URD §2.3 into TypeScript would be a third copy of the matrix
 * (after the URD and the kernel) and the one nobody's test parses the spec to
 * check. The cost is one small call per navigation, bounded by
 * `ADMIN_PORTAL_SESSION_CACHE_SECONDS`.
 *
 * **It is still not authorization**, and AL-06 says so in as many words (US-21.1):
 * every endpoint re-decides for itself. What this stops is a console that offers a
 * screen whose every request would be refused — and a stale cache entry costs at
 * most a screen that renders and then 403s its own data.
 */

/** Cached session evaluations, keyed by bearer. Bounded in size and in age. */
const sessionCache = new Map<string, { expiresAt: number; session: AdminSession | null }>();
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

  const response = isReachable(session.menu, path)
    ? NextResponse.next({ request: { headers } })
    : // The rewrite keeps the operator on the URL they asked for — a redirect
      // would rewrite their address bar and lose what they were trying to reach.
      // `/denied` calls `forbidden()`, which is what makes the status a real 403
      // rather than a 200 whose body says no.
      NextResponse.rewrite(new URL('/denied', request.url), { request: { headers } });

  if (rotated) writeRotatedTokens(response, rotated);
  return response;
}

/* ------------------------------------------------------------------------- */

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

async function evaluate(accessToken: string): Promise<AdminSession | null> {
  const now = Date.now();
  const hit = sessionCache.get(accessToken);
  if (hit && hit.expiresAt > now) return hit.session;

  let session: AdminSession | null;
  try {
    const { data } = await apiFetch<AdminSession>({
      path: '/v1/admin/session',
      accessToken,
    });
    session = data;
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
