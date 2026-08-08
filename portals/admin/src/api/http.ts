import 'server-only';

import { apiBaseUrl, MissingConfigurationError } from '@/config/env';

import { localProblem, ProblemError, readProblem } from './problem';

/**
 * The one place in the portal that calls `fetch` against MageRide.
 *
 * **The browser never talks to the gateway.** Every request leaves the Next server,
 * which is what lets the session live in an httpOnly cookie the page's JavaScript
 * cannot read: an access token in `localStorage` is one XSS away from being an
 * operator console session, and this console can suspend drivers and reverse fees.
 * `import 'server-only'` makes that a build error rather than a convention — a
 * client component that imports this module fails to compile.
 *
 * Nothing here knows about credentials. The bearer is supplied by
 * `@/server/session`, and the two auth routes that have no bearer yet
 * (`/v1/admin/auth/login`, `/v1/auth/refresh`) use the same function with none.
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
 * `cache: 'no-store'` on every request, including reads. An operator console is a
 * window onto other people's records under a per-caller RBAC evaluation; a cache
 * entry keyed on the URL alone is one Verification Officer seeing a Finance
 * Officer's page. Next 15+ defaults to no-store, and stating it means a framework
 * default that moves cannot quietly move this.
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
      throw new ProblemError(
        localProblem('service-unavailable', 503, request.path, error.message),
      );
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

  // `GET /v1/admin/documents/{id}` answers 302 with a signed URL, and C063 records
  // the DOC_VIEW row on the way out of exactly that response. Following it here
  // would put the object bytes through this process; handing the caller the
  // Location keeps them on the far side of the perimeter.
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

export interface ApiDownloadRequest {
  /** An absolute API path, `/v1/...`. */
  readonly path: string;
  /** The media type this route answers — `text/csv`, `application/pdf`. */
  readonly accept: string;
  readonly accessToken?: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
}

export interface ApiDownload {
  readonly status: number;
  readonly body: ArrayBuffer;
  /** What the platform said the bytes are. Never guessed from the path. */
  readonly contentType: string;
  /** The filename the platform named, when it named one. */
  readonly filename?: string;
}

/**
 * A GET whose answer is a **file**, not JSON.
 *
 * It exists because {@link apiFetch} parses, and a CSV put through `JSON.parse`
 * is a `SyntaxError` where a download should have been. Everything else is
 * deliberately the same function's behaviour — the same origin, the same bearer,
 * the same `no-store`, the same `problem+json` on a failure — because a second
 * way out of this process is exactly what `test/fences.test.ts` exists to prevent.
 * That test names this function alongside `apiFetch`; a third one has to be added
 * to it on purpose.
 *
 * **The bytes are relayed, never re-rendered.** admin-bff builds
 * `stats.csv` from the same `IDashboardStatsService` call that answers
 * `/dashboard/stats`, so "the export matches the screen" holds because there is
 * one computation. A portal that formatted its own CSV from the JSON would be a
 * second implementation of the same file, and the two would diverge the first time
 * either changed.
 */
export async function apiDownload(request: ApiDownloadRequest): Promise<ApiDownload> {
  let url: string;
  try {
    url = buildUrl(request.path, request.searchParams);
  } catch (error) {
    if (error instanceof MissingConfigurationError) {
      throw new ProblemError(localProblem('service-unavailable', 503, request.path, error.message));
    }
    throw error;
  }

  const headers = new Headers({
    // The problem type is accepted too: a refusal is still JSON, whatever the
    // route's success body is.
    accept: `${request.accept}, application/problem+json`,
  });
  if (request.accessToken) headers.set('authorization', `Bearer ${request.accessToken}`);

  let response: Response;
  try {
    response = await fetch(url, {
      method: 'GET',
      headers,
      cache: 'no-store',
      redirect: 'manual',
      signal: request.signal,
    });
  } catch (error) {
    throw new ProblemError(
      localProblem(
        'dependency-unavailable',
        503,
        request.path,
        error instanceof Error ? error.message : 'The MageRide API could not be reached.',
      ),
    );
  }

  // A download route that redirects is not one this portal follows: the bytes
  // would be fetched from an origin nothing here vetted. No `/v1/admin/**` export
  // does — `apiFetch` handles the one route that 302s (C063's document viewer) by
  // handing the caller the `Location` — so this is a refusal, not a case with a
  // design behind it.
  if (response.status >= 300 && response.status < 400) {
    throw new ProblemError(
      localProblem(
        'dependency-unavailable',
        502,
        request.path,
        `The platform answered ${response.status} with a redirect this route cannot follow.`,
      ),
    );
  }

  if (!response.ok) {
    throw new ProblemError(await readProblem(response, request.path));
  }

  const filename = filenameFrom(response.headers.get('content-disposition'));

  return {
    status: response.status,
    body: await response.arrayBuffer(),
    contentType: response.headers.get('content-type') ?? request.accept,
    ...(filename ? { filename } : {}),
  };
}

export interface ApiUploadRequest {
  /** An absolute API path, `/v1/...`. */
  readonly path: string;
  /**
   * The request body, **as a stream**, and the caller's own — not a copy of it.
   *
   * A GTFS feed is up to 200 MB (BR-32.1). Buffering it here would put the whole
   * file in this process's memory on a route whose only job is to carry it, which
   * is the same argument `GtfsProxyEndpoints` makes for streaming on the other
   * side of the same hop.
   */
  readonly body: ReadableStream<Uint8Array>;
  /** The inbound `Content-Type`, boundary and all. A multipart body without it is unreadable. */
  readonly contentType: string;
  readonly accessToken?: string;
  /** R-14/R-18 replay key. Required — every mutation carries one. */
  readonly idempotencyKey: string;
  readonly signal?: AbortSignal;
}

/**
 * A POST whose body is **bytes somebody is still sending**, as the signed-in
 * operator.
 *
 * The fourth and last member of the transport, and the third the fences test
 * names. It exists for one route — SCR-AP-016's `POST …/gtfs/uploads` — and the
 * reason it cannot be {@link apiFetch} is that `apiFetch` serialises its body to
 * JSON, which is the correct behaviour for every other mutation this console
 * makes and the wrong one for a zip.
 *
 * Everything else is deliberately the same function's behaviour: same origin,
 * same bearer, same `no-store`, same `problem+json` on a failure. `duplex: 'half'`
 * is what lets a `ReadableStream` be a request body at all — the sender finishes
 * before the receiver starts — and it is absent from the DOM's `RequestInit`
 * because it is an HTTP/1.1 streaming concern the browser has never needed.
 *
 * **`Content-Length` is deliberately not forwarded.** A streamed body is sent
 * chunked, and a declared length that disagrees with the framing is the classic
 * proxy failure. The 200 MB ceiling is still enforced three times over — by the
 * dropzone before a byte leaves the browser, by the route handler against the
 * declared length before this is called, and by transit-svc's own Kestrel limit
 * and object store.
 */
export async function apiUpload<T>(request: ApiUploadRequest): Promise<ApiResponse<T>> {
  let url: string;
  try {
    url = buildUrl(request.path, undefined);
  } catch (error) {
    if (error instanceof MissingConfigurationError) {
      throw new ProblemError(localProblem('service-unavailable', 503, request.path, error.message));
    }
    throw error;
  }

  const headers = new Headers({
    accept: 'application/json, application/problem+json',
    'content-type': request.contentType,
    'idempotency-key': request.idempotencyKey,
  });
  if (request.accessToken) headers.set('authorization', `Bearer ${request.accessToken}`);

  let response: Response;
  try {
    response = await fetch(url, {
      method: 'POST',
      headers,
      body: request.body,
      cache: 'no-store',
      redirect: 'manual',
      signal: request.signal,
      // Not on the DOM lib's `RequestInit`; undici requires it for a stream body.
      duplex: 'half',
    } as RequestInit & { duplex: 'half' });
  } catch (error) {
    throw new ProblemError(
      localProblem(
        'dependency-unavailable',
        503,
        request.path,
        error instanceof Error ? error.message : 'The MageRide API could not be reached.',
      ),
    );
  }

  if (!response.ok) {
    throw new ProblemError(await readProblem(response, request.path));
  }

  const text = await response.text();
  if (!text) return { status: response.status, data: undefined as T };

  return { status: response.status, data: JSON.parse(text) as T };
}

/**
 * The `filename` out of a `Content-Disposition`, if it is a plain one.
 *
 * Anything with a path separator or a quote in it is dropped rather than
 * sanitised: the value is a header from upstream and the portal puts it straight
 * back into a header of its own, so the safe reading of an unexpected one is that
 * there is no filename and the caller names the file itself.
 */
function filenameFrom(header: string | null): string | undefined {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header ?? '');
  const name = match?.[1]?.trim();
  if (!name || /["\\/\r\n]/.test(name)) return undefined;

  try {
    return decodeURIComponent(name);
  } catch {
    // A stray `%` is not an escape. The header is still a filename; it is just
    // not a percent-encoded one.
    return name;
  }
}
