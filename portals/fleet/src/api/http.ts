import 'server-only';

import { apiBaseUrl, MissingConfigurationError } from '@/config/env';

import { localProblem, ProblemError, readProblem } from './problem';

/**
 * The one place in the portal that calls `fetch` against MageRide.
 *
 * **The browser never talks to the gateway.** Every request leaves the Next server,
 * which is what lets the session live in an httpOnly cookie the page's JavaScript
 * cannot read: an access token in `localStorage` is one XSS away from being a
 * session that can onboard vehicles, assign drivers and spend an organisation's
 * wallet. `import 'server-only'` makes that a build error rather than a convention
 * — a client component that imports this module fails to compile.
 *
 * Nothing here knows about credentials. The bearer is supplied by
 * `@/server/session`, and the four auth routes that have no bearer yet
 * (`/v1/auth/password`, `/v1/auth/google`, `/v1/auth/apple`, `/v1/auth/refresh`)
 * use the same function with none.
 */

export interface ApiRequest {
  /** An absolute API path, `/v1/...`. */
  readonly path: string;
  readonly method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  readonly body?: unknown;
  /** Bearer to present. Omitted on the sign-in and refresh routes. */
  readonly accessToken?: string;
  /** R-14/R-18 replay protection. Set on every mutation by `mutate`. */
  readonly idempotencyKey?: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
  /** Extra headers. Never a second `Authorization`. */
  readonly headers?: Readonly<Record<string, string>>;
}

export interface ApiResponse<T> {
  readonly status: number;
  readonly data: T;
}

function buildUrl(path: string, searchParams: ApiRequest['searchParams']): string {
  if (!path.startsWith('/')) {
    throw new TypeError(`API path must be absolute, got "${path}".`);
  }

  const url = new URL(apiBaseUrl() + path);
  for (const [key, value] of Object.entries(searchParams ?? {})) {
    if (value !== undefined) url.searchParams.set(key, String(value));
  }
  return url.toString();
}

/**
 * Performs the call and returns the parsed body, or throws {@link ProblemError}.
 *
 * `cache: 'no-store'` on every request, including reads. A fleet console is a
 * window onto one organisation's records under a per-caller, row-level-security
 * evaluation; a cache entry keyed on the URL alone is one organisation seeing
 * another's. Next 15+ defaults to no-store, and stating it means a framework
 * default that moves cannot quietly move this.
 *
 * **No `X-Platform` header is ever set**, and that is load-bearing rather than an
 * omission: `AuthEndpoints.RefuseApps` refuses `/v1/auth/{password,google,apple}`
 * for an `android` or `ios` platform, because AL-07 gives the two apps Phone OTP
 * and nothing else. A browser sends no platform at all, and so does this.
 */
export async function apiFetch<T>(request: ApiRequest): Promise<ApiResponse<T>> {
  const method = request.method ?? 'GET';

  let url: string;
  try {
    url = buildUrl(request.path, request.searchParams);
  } catch (error) {
    if (error instanceof MissingConfigurationError) {
      // A deployment with no gateway address is not a bad request from the
      // operator; it is this process being unable to serve. 503 is what that is.
      throw new ProblemError(localProblem('service-unavailable', 503, request.path, error.message));
    }
    throw error;
  }

  const headers = new Headers({
    accept: 'application/json, application/problem+json',
    ...request.headers,
  });

  if (request.accessToken) headers.set('authorization', `Bearer ${request.accessToken}`);
  if (request.idempotencyKey) headers.set('idempotency-key', request.idempotencyKey);
  if (request.body !== undefined) headers.set('content-type', 'application/json');

  let response: Response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      cache: 'no-store',
      redirect: 'manual',
      signal: request.signal,
    });
  } catch (error) {
    // A refused connection, a DNS failure or a timeout. The operator is told the
    // platform is unreachable; `detail` carries what actually happened for the log.
    throw new ProblemError(
      localProblem(
        'dependency-unavailable',
        503,
        request.path,
        error instanceof Error ? error.message : 'The MageRide API could not be reached.',
      ),
    );
  }

  // A document route answers 302 with a signed object-storage URL, and the
  // service records the view on the way out of exactly that response. Following
  // it here would put the object bytes through this process; handing the caller
  // the `Location` keeps them on the far side of the perimeter.
  if (response.status >= 300 && response.status < 400) {
    return { status: response.status, data: { location: response.headers.get('location') } as T };
  }

  if (!response.ok) {
    throw new ProblemError(await readProblem(response, request.path));
  }

  if (response.status === 204) {
    return { status: 204, data: undefined as T };
  }

  const text = await response.text();
  if (!text) return { status: response.status, data: undefined as T };

  return { status: response.status, data: JSON.parse(text) as T };
}
