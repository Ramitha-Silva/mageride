/**
 * The portal's cookie vocabulary — names, options and the two encodings — in one
 * module with **no framework imports**.
 *
 * It has none because two different worlds set these cookies: `proxy.ts` writes
 * them through `NextResponse.cookies` when it rotates a token, and the sign-in and
 * preference server actions write them through `next/headers`. `next/headers` is
 * not available in the proxy, so anything both sides share has to be importable by
 * both — which means constants and pure functions, and nothing else.
 */

/** RS256 access JWT, 30-minute lifetime (D-29). */
export const ACCESS_TOKEN_COOKIE = 'mr_admin_at';

/** Opaque rotating refresh token, single-use (D-29). */
export const REFRESH_TOKEN_COOKIE = 'mr_admin_rt';

/** Unix seconds at which the access token expires — so rotation is scheduled, not guessed. */
export const EXPIRES_AT_COOKIE = 'mr_admin_exp';

/** The signed-in staff member's own display identity. See {@link encodeIdentity}. */
export const IDENTITY_COOKIE = 'mr_admin_who';

/** Chosen display language, overriding the account's stored one for this browser. */
export const LOCALE_COOKIE = 'mr_admin_locale';

/** `light` | `dark` | `system`. */
export const THEME_COOKIE = 'mr_admin_theme';

/** CSRF state for the Google authorization-code round trip. */
export const OAUTH_STATE_COOKIE = 'mr_admin_oauth_state';

/** Every cookie a sign-out clears. */
export const SESSION_COOKIES = [
  ACCESS_TOKEN_COOKIE,
  REFRESH_TOKEN_COOKIE,
  EXPIRES_AT_COOKIE,
  IDENTITY_COOKIE,
] as const;

/**
 * The header `proxy.ts` stamps a freshly rotated access token onto.
 *
 * A rotation happens in the proxy, but the render that follows it happens before
 * the browser has been handed the new `Set-Cookie` — so the render would otherwise
 * read the spent token out of the request's own cookie jar and 401 on its first
 * call. The header is how the two halves of one request agree.
 */
export const ACCESS_TOKEN_HEADER = 'x-mr-access-token';

/**
 * The header `proxy.ts` stamps the request path onto.
 *
 * App Router layouts are given `params`, never the pathname, and the shell has to
 * know which nav entry is current. Reading it from a header the proxy sets is the
 * documented way round that, and it costs nothing because the proxy has already
 * parsed the URL to make the AL-06 decision.
 */
export const PATHNAME_HEADER = 'x-mr-pathname';

/** Cookie options every session cookie shares. */
export function sessionCookieOptions(secure: boolean, maxAgeSeconds?: number) {
  return {
    httpOnly: true,
    secure,
    // Lax rather than Strict: the Google sign-in redirect arrives as a top-level
    // cross-site GET, and Strict would drop the state cookie on exactly the
    // request that has to check it.
    sameSite: 'lax' as const,
    path: '/',
    ...(maxAgeSeconds === undefined ? {} : { maxAge: maxAgeSeconds }),
  };
}

/**
 * The staff member's own name, address and stored language, as the sign-in
 * response gave them.
 *
 * Kept in a cookie rather than re-fetched: it is the caller's own identity, it is
 * three short strings, and the alternative is a `GET /v1/users/me` on **every**
 * page render for a value that changes when somebody edits their own profile. It
 * is httpOnly like the rest — nothing in the browser needs to read it, and a
 * readable cookie carrying an operator's email is a cookie an XSS can exfiltrate.
 * The cost is stated rather than hidden: a name changed mid-session shows the old
 * one until the next sign-in.
 */
export interface DisplayIdentity {
  readonly name?: string;
  readonly email?: string;
  readonly language?: string;
}

export function encodeIdentity(identity: DisplayIdentity): string {
  return Buffer.from(JSON.stringify(identity), 'utf8').toString('base64url');
}

export function decodeIdentity(value: string | undefined): DisplayIdentity | null {
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(Buffer.from(value, 'base64url').toString('utf8'));
    if (!parsed || typeof parsed !== 'object') return null;
    const { name, email, language } = parsed as DisplayIdentity;
    return {
      ...(typeof name === 'string' ? { name } : {}),
      ...(typeof email === 'string' ? { email } : {}),
      ...(typeof language === 'string' ? { language } : {}),
    };
  } catch {
    // A cookie this process cannot read is a cookie from an older encoding or a
    // tampered one. Neither is an error worth showing: the chrome falls back to
    // the role name and everything else still works.
    return null;
  }
}
