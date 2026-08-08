import type { ProviderConfig } from '@/config/env';

/**
 * The two federated sign-in arms AL-07 gives the Fleet Portal, as data.
 *
 * ## Why both are the implicit **ID-token** flow rather than an authorization code
 *
 * iam-svc's routes take `{idToken}` and verify it against the provider's JWKS
 * (`POST /v1/auth/google`, `POST /v1/auth/apple`). Turning an authorization code
 * into an ID token needs the client secret, and the two places that could hold it
 * are this portal and iam-svc. iam-svc holds one for the *Admin* Portal's Google
 * arm and exposes it as `POST /v1/admin/auth/login {googleAuthCode}` — a route
 * that forces `app=admin` and would refuse a fleet account, so it is not
 * available here. Putting a second copy of the secret in this process would make
 * the portal a second place a Google identity becomes a MageRide session, which
 * is exactly what C104 refused to build.
 *
 * So the browser asks the provider for a signed ID token, the provider posts it
 * back, and this process relays it. The portal holds no secret, mints nothing and
 * validates nothing — iam-svc does, because it is the only side that can.
 *
 * ## Why `response_mode=form_post`, and what it costs
 *
 * Apple requires it for any `response_type` containing `id_token`. Google's
 * implicit response offers `fragment` — which never reaches a server — or
 * `form_post`. So both arms come back as a **cross-site POST**, which is why the
 * state cookie is `SameSite=None` (see `oauthStateCookieOptions`) and why the
 * callback is a POST handler.
 *
 * ## What `state` does and does not prove
 *
 * The nonce in the state cookie is checked against the `state` parameter, which is
 * the login-CSRF defence: without it, any page on the internet could post its own
 * `id_token` here and sign a fleet operator into somebody else's account. The
 * `nonce` parameter is sent as well, and it is bound into the token by the
 * provider — but **this portal does not verify it**, because verifying it would
 * mean parsing an unverified JWT, and the portal deliberately never reads a token.
 * Whoever validates the signature is the side that can check the nonce; that is
 * iam-svc, and it is recorded in the C111 handoff as a thing to ask of it.
 */

export type ProviderId = 'google' | 'apple';

export const PROVIDER_IDS: readonly ProviderId[] = ['google', 'apple'];

export function isProviderId(value: string): value is ProviderId {
  return (PROVIDER_IDS as readonly string[]).includes(value);
}

interface ProviderSpec {
  readonly authorizeUrl: string;
  /** `response_type`, verbatim. Apple will not issue an ID token for `id_token` alone. */
  readonly responseType: string;
  readonly scope: string;
  /** Extra parameters this provider alone takes. */
  readonly extra?: Readonly<Record<string, string>>;
}

const PROVIDERS: Readonly<Record<ProviderId, ProviderSpec>> = {
  google: {
    authorizeUrl: 'https://accounts.google.com/o/oauth2/v2/auth',
    responseType: 'id_token',
    scope: 'openid email profile',
    // Fleet offices share machines. Forcing the chooser stops a second person
    // silently landing in the first one's account.
    extra: { prompt: 'select_account' },
  },
  apple: {
    authorizeUrl: 'https://appleid.apple.com/auth/authorize',
    // `code id_token` rather than `id_token`: Apple issues an identity token only
    // for a response type that also asks for a code. The code is discarded — this
    // process has no secret with which to redeem it, and does not need to.
    responseType: 'code id_token',
    // `email` and nothing more. Apple returns the display name once, on the first
    // authorization, in a separate field this portal has no use for; the email is
    // what iam-svc matches an account on.
    scope: 'email',
  },
};

/** The URL the browser is sent to. `state` is the nonce; `nonce` is bound into the token. */
export function authorizeUrl(
  provider: ProviderId,
  config: ProviderConfig,
  nonce: string,
): string {
  const spec = PROVIDERS[provider];
  const url = new URL(spec.authorizeUrl);

  url.searchParams.set('client_id', config.clientId);
  url.searchParams.set('redirect_uri', config.redirectUri);
  url.searchParams.set('response_type', spec.responseType);
  url.searchParams.set('response_mode', 'form_post');
  url.searchParams.set('scope', spec.scope);
  url.searchParams.set('state', nonce);
  url.searchParams.set('nonce', nonce);

  for (const [key, value] of Object.entries(spec.extra ?? {})) {
    url.searchParams.set(key, value);
  }

  return url.toString();
}

/** What the portal remembers between the two legs: a nonce, and where to go afterwards. */
export interface OAuthState {
  readonly nonce: string;
  readonly next?: string;
}

export function encodeState(state: OAuthState): string {
  return Buffer.from(JSON.stringify(state), 'utf8').toString('base64url');
}

export function decodeState(value: string | undefined): OAuthState | null {
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

/**
 * Constant-time-ish comparison of the returned `state` against the stored nonce.
 *
 * Length is compared first and the loop always runs to the end of the shorter
 * string, so the check does not return early on the first differing character.
 * The value is a UUID with 122 bits of entropy and ten minutes to live, so this is
 * belt rather than braces — but a security comparison that short-circuits is the
 * kind of thing worth simply not writing.
 */
export function stateMatches(returned: string | null | undefined, stored: string): boolean {
  if (!returned || returned.length !== stored.length) return false;

  let difference = 0;
  for (let index = 0; index < stored.length; index += 1) {
    difference |= returned.charCodeAt(index) ^ stored.charCodeAt(index);
  }
  return difference === 0;
}
