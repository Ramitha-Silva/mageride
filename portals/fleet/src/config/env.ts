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
      `${variable} is not set. The Fleet Portal cannot reach the MageRide API without it ` +
        '(see portals/fleet/.env.example).',
    );
    this.name = 'MissingConfigurationError';
  }
}

/**
 * The C008 gateway origin. One host for every service: `/v1/fleets/**` reaches
 * fleet-svc (and, at Order 20, fleet-health-svc and fleet-billing-svc for three
 * of its sub-paths), `/v1/auth/**` and `/v1/me/**` reach iam-svc. The portal is
 * not the place that knows which process answers what
 * (`backend/src/ApiGateway/gateway-routes.json`).
 */
export function apiBaseUrl(): string {
  const value = process.env.MAGERIDE_API_BASE_URL?.trim();
  if (!value) throw new MissingConfigurationError('MAGERIDE_API_BASE_URL');
  return value.replace(/\/+$/, '');
}

/**
 * One federated sign-in provider, as it is configured for this deployment.
 *
 * `clientId` is the OIDC client the **provider** knows (Google's OAuth client id,
 * Apple's Services ID). `redirectUri` must be registered on that client *and*
 * equal what iam-svc expects to see, because both providers match it byte for
 * byte before they will hand anything back.
 */
export interface ProviderConfig {
  readonly clientId: string;
  readonly redirectUri: string;
}

/** Google Sign-In (AL-07). Absent ⇒ the button is not rendered at all. */
export function googleSignIn(): ProviderConfig | null {
  return pair('GOOGLE_OIDC_CLIENT_ID', 'GOOGLE_OIDC_REDIRECT_URI');
}

/** Sign in with Apple (AL-07 — the Fleet Portal is the only surface that has it). */
export function appleSignIn(): ProviderConfig | null {
  return pair('APPLE_OIDC_CLIENT_ID', 'APPLE_OIDC_REDIRECT_URI');
}

/**
 * Both or neither. A client id with no redirect URI produces an authorize URL the
 * provider refuses, which reads to the operator as "Google sign-in is broken"
 * rather than "Google sign-in is not configured" — and a control that cannot work
 * is worse than no control, because nobody can tell the two apart.
 */
function pair(clientIdVariable: string, redirectUriVariable: string): ProviderConfig | null {
  const clientId = process.env[clientIdVariable]?.trim();
  const redirectUri = process.env[redirectUriVariable]?.trim();

  if (!clientId || !redirectUri) return null;
  return { clientId, redirectUri };
}

/**
 * The MapLibre **style document** SCR-FP-007 draws its basemap from — D-14's
 * `tile-cdn`, which is a Cloudflare R2 bucket of PMTiles behind a Worker that
 * serves range-byte requests (ADD §6, §10.2). Δ C114.
 *
 * The one URL in this portal a **browser** fetches, and it is not the platform:
 * tiles are static cartography on a CDN, they carry no organisation's data and no
 * bearer travels with them. Everything about the fleet still leaves the Next
 * server — the positions are rendered from `GET /v1/fleets/{id}/map`, read
 * server-side like every other call. It is passed to the map component as a prop
 * rather than inlined into the client bundle as a build-time public variable —
 * which is what keeps the shell's rule that this portal has none of those true.
 * (`test/fences.test.ts` greps the raw source for that prefix, comments
 * included, so this paragraph does not spell it.)
 *
 * **Optional, and unset is a supported state.** The map then renders the fleet's
 * positions on an empty canvas rather than failing: the markers are the org's own
 * data and are what the screen is for, and MapLibre with no basemap is a worse map
 * but a working one. The screen says which of the two it is drawing, so nobody
 * reports a blank basemap as missing vehicles. `tile-cdn` is also the one
 * dependency ADD §14 already plans an outage for.
 */
export function mapStyleUrl(): string | null {
  return process.env.FLEET_PORTAL_MAP_STYLE_URL?.trim() || null;
}

export function cookiesAreSecure(): boolean {
  return process.env.FLEET_PORTAL_COOKIE_SECURE?.trim().toLowerCase() !== 'false';
}

function seconds(variable: string, fallback: number): number {
  const raw = process.env[variable]?.trim();
  if (!raw) return fallback;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
}

/** How early the proxy rotates an access token (D-29 gives it 30 minutes). */
export function refreshSkewSeconds(): number {
  return seconds('FLEET_PORTAL_REFRESH_SKEW_SECONDS', 120);
}

/** How long the proxy may reuse one caller's evaluated seat and org status. */
export function sessionCacheSeconds(): number {
  return seconds('FLEET_PORTAL_SESSION_CACHE_SECONDS', 15);
}
