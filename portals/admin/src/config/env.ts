/**
 * Every environment value the portal reads, resolved in one place and read at
 * request time rather than at module load.
 *
 * Read at request time on purpose: the portal ships as one `output: 'standalone'`
 * image and the same image runs on the lightweight replica and on DOKS with
 * different values (CLAUDE.md, Infra). A value captured into a module constant is
 * captured at build, which would bake the replica's gateway address into the
 * production image.
 */

/** Thrown when a required variable is missing. Surfaced as 503, never as a 500. */
export class MissingConfigurationError extends Error {
  constructor(readonly variable: string) {
    super(
      `${variable} is not set. The Admin Portal cannot reach the MageRide API without it ` +
        '(see portals/admin/.env.example).',
    );
    this.name = 'MissingConfigurationError';
  }
}

/**
 * The C008 gateway origin. One host for every service: `/v1/admin/**` reaches
 * admin-bff and `/v1/admin/auth/**` + `/v1/auth/**` reach iam-svc, because the
 * gateway routes the auth sub-tree past the BFF at Order 20
 * (`backend/src/ApiGateway/gateway-routes.json`). The portal is not the place
 * that knows which process answers what.
 */
export function apiBaseUrl(): string {
  const value = process.env.MAGERIDE_API_BASE_URL?.trim();
  if (!value) throw new MissingConfigurationError('MAGERIDE_API_BASE_URL');
  return value.replace(/\/+$/, '');
}

/** Google Sign-In (AL-07). Absent ⇒ the button is not rendered at all. */
export function googleSignIn(): { clientId: string; redirectUri: string } | null {
  const clientId = process.env.GOOGLE_OIDC_CLIENT_ID?.trim();
  const redirectUri = process.env.GOOGLE_OIDC_REDIRECT_URI?.trim();

  // Both or neither. A client id with no redirect URI produces an authorize URL
  // Google refuses, which reads to the operator as "Google sign-in is broken"
  // rather than "Google sign-in is not configured".
  if (!clientId || !redirectUri) return null;
  return { clientId, redirectUri };
}

export function cookiesAreSecure(): boolean {
  return process.env.ADMIN_PORTAL_COOKIE_SECURE?.trim().toLowerCase() !== 'false';
}

function seconds(variable: string, fallback: number): number {
  const raw = process.env[variable]?.trim();
  if (!raw) return fallback;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
}

/** How early the proxy rotates an access token (D-29 gives it 30 minutes). */
export function refreshSkewSeconds(): number {
  return seconds('ADMIN_PORTAL_REFRESH_SKEW_SECONDS', 120);
}

/** How long the proxy may reuse one caller's evaluated permissions. */
export function sessionCacheSeconds(): number {
  return seconds('ADMIN_PORTAL_SESSION_CACHE_SECONDS', 15);
}
