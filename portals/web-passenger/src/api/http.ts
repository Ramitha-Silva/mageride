import 'server-only';

import { apiBaseUrl, MissingConfigurationError } from '@/config/env';

import { localProblem, ProblemError, readProblem } from './problem';

/**
 * The one place in this application that calls `fetch` against MageRide.
 *
 * **The browser never talks to the gateway**, exactly as on the other two portals
 * — but for a different reason, and it is worth being precise about which.
 * There is no session here to protect: the share token is in the visitor's own
 * address bar and is the whole credential. What this keeps out of the browser is
 * the *platform*: a page opened from an SMS on a stranger's phone talks to
 * `passenger.mageride.lk` and to nothing else, so there is no second origin to
 * reach, no CORS policy to widen and no gateway address published in a script.
 * `import 'server-only'` makes that a build error rather than a convention.
 *
 * Nothing here knows about credentials, because on this surface there is no header
 * to attach. `/public/track/{token}` carries its credential in the path and
 * public-bff registers no authentication scheme at all.
 */

export interface ApiRequest {
  /** An absolute API path, `/public/...`. */
  readonly path: string;
  readonly method?: 'GET' | 'POST';
  readonly body?: unknown;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
  /** Extra request headers — the live feed's `Last-Event-ID`, and nothing else so far. */
  readonly headers?: Readonly<Record<string, string>>;
  /** What to ask for. Defaults to JSON; the live feed asks for `text/event-stream`. */
  readonly accept?: string;
  /**
   * Hand back the `Response` itself rather than a parsed body.
   *
   * The one caller is the live feed: an SSE body is a stream that stays open for
   * minutes and is proxied through to the browser a frame at a time. Reading it
   * into memory would defeat the entire point of the transport — the page would
   * receive the whole feed at once, when it ended.
   */
  readonly stream?: boolean;
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
 * `cache: 'no-store'` on every request, including reads. Two reasons, and the
 * second is the one that matters here: a tracking page whose position could be
 * served from a cache would draw a marker that is not where the vehicle is, and
 * **a cache entry keyed on the URL alone is one share token's answer served to the
 * next request that presents it** — which is fine, since the URL *is* the token,
 * right up until the token is revoked and the cache has not noticed. Next 15+
 * defaults to no-store, and stating it means a framework default that moves
 * cannot quietly move this.
 *
 * **No `X-Platform` header is ever set.** D-30's attestation middleware treats an
 * `android`/`ios` platform as a claim about an attested app; a browser is not one
 * and sends no platform at all, which is the honest thing for it to do.
 */
export async function apiFetch<T>(request: ApiRequest): Promise<ApiResponse<T>> {
  const method = request.method ?? 'GET';

  let url: string;
  try {
    url = buildUrl(request.path, request.searchParams);
  } catch (error) {
    if (error instanceof MissingConfigurationError) {
      // A deployment with no gateway address is not a bad request from the
      // visitor; it is this process being unable to serve. 503 is what that is.
      throw new ProblemError(localProblem('service-unavailable', 503, request.path, error.message));
    }
    throw error;
  }

  const headers = new Headers({
    // `application/problem+json` is always acceptable, whatever the success type
    // is: D3' §0 makes every error that shape and the SSE route is no exception.
    accept: request.accept
      ? `${request.accept}, application/problem+json`
      : 'application/json, application/problem+json',
    ...request.headers,
  });

  // **No `Idempotency-Key` is sent, and that is a decision rather than an
  // omission.** public-bff derives one from the business fact when the page sends
  // none, and its derivation is better than anything this side could compose:
  // `pickup:{verb}:{token}` is stable for ever, because a location request can be
  // answered exactly once and a retried tap should replay rather than read a
  // refusal — while `sos:{window}:{token}` is deliberately *windowed*, because a
  // stable key would make a second genuine emergency twenty minutes later replay
  // the first and send nobody anything. A fresh UUID per attempt would replace
  // both with a key that dedupes nothing.
  if (request.body !== undefined) headers.set('content-type', 'application/json');

  let response: Response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      cache: 'no-store',
      redirect: 'manual',
      ...(request.signal ? { signal: request.signal } : {}),
    });
  } catch (error) {
    // A refused connection, a DNS failure or a timeout. `detail` carries what
    // actually happened, for the log rather than for the page.
    throw new ProblemError(
      localProblem(
        'dependency-unavailable',
        503,
        request.path,
        error instanceof Error ? error.message : 'public-bff could not be reached.',
      ),
    );
  }

  if (!response.ok) {
    throw new ProblemError(await readProblem(response, request.path));
  }

  if (request.stream) {
    return { status: response.status, data: response as T };
  }

  if (response.status === 204) {
    return { status: 204, data: undefined as T };
  }

  const text = await response.text();
  if (!text) return { status: response.status, data: undefined as T };

  return { status: response.status, data: JSON.parse(text) as T };
}
