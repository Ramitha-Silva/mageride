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
  /**
   * Serialised as JSON — **unless it is a `FormData`**, which is sent as the
   * multipart body it already is.
   *
   * Δ C112. `POST /v1/fleets/{id}/payout-profile/documents` is the portal's one
   * `multipart/form-data` route (AL-49: the bank statement or passbook page, and
   * the bank-app LankaQR image), and fleet-svc's handler reads `kind` and `file`
   * off the form. Everything else about the call is unchanged — same origin, same
   * bearer, same `Idempotency-Key`, same `no-store`, same problem+json on failure
   * — so it stays this function rather than becoming a second transport.
   */
  readonly body?: unknown;
  /** Bearer to present. Omitted on the sign-in and refresh routes. */
  readonly accessToken?: string;
  /** R-14/R-18 replay protection. Set on every mutation by `mutate`. */
  readonly idempotencyKey?: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
  /** Extra headers. Never a second `Authorization`. */
  readonly headers?: Readonly<Record<string, string>>;
  /**
   * Read the body as **bytes** rather than as JSON, and answer an
   * {@link ApiDocument}.
   *
   * Δ C115. `GET …/billing/{invoiceId}/export` answers `text/csv` or
   * `application/pdf` — the invoice document fleet-billing-svc renders, whose CSV
   * "prints money twice … because a spreadsheet's floating-point sum must never be
   * the authority on somebody's bill". Parsing that as JSON throws a `SyntaxError`
   * on the first byte, and re-implementing the document in this portal would be a
   * second file about the same money.
   *
   * A failure is still `application/problem+json` and is still read as one: this
   * flag changes how a **success** is decoded and nothing else.
   */
  readonly binary?: boolean;
  /** What to ask for. Defaults to JSON; a document route asks for its own types. */
  readonly accept?: string;
}

export interface ApiResponse<T> {
  readonly status: number;
  readonly data: T;
}

/** A document a route answers with, rather than a body to parse. Δ C115. */
export interface ApiDocument {
  readonly bytes: ArrayBuffer;
  readonly contentType: string;
  /** The name the **service** gave it in `Content-Disposition`, if it gave one. */
  readonly filename: string | null;
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
    // `application/problem+json` is always acceptable, whatever the success type
    // is: D3' §0 makes every error that shape and a document route is no exception.
    accept: request.accept
      ? `${request.accept}, application/problem+json`
      : 'application/json, application/problem+json',
    ...request.headers,
  });

  if (request.accessToken) headers.set('authorization', `Bearer ${request.accessToken}`);
  if (request.idempotencyKey) headers.set('idempotency-key', request.idempotencyKey);
  // A `FormData` body carries its own content type, and the multipart boundary is
  // part of it — set `application/json` over one and the far side reads a body it
  // cannot parse. `undici` writes the header when it is left alone, so the one
  // correct thing to do here is nothing. See {@link ApiRequest.body}.
  if (request.body !== undefined && !isMultipart(request.body)) {
    headers.set('content-type', 'application/json');
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: encodeBody(request.body),
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

  if (request.binary) {
    return {
      status: response.status,
      data: {
        bytes: await response.arrayBuffer(),
        contentType: response.headers.get('content-type') ?? 'application/octet-stream',
        filename: filenameFrom(response.headers.get('content-disposition')),
      } as T,
    };
  }

  if (response.status === 204) {
    return { status: 204, data: undefined as T };
  }

  const text = await response.text();
  if (!text) return { status: response.status, data: undefined as T };

  return { status: response.status, data: JSON.parse(text) as T };
}

/**
 * The filename out of a `Content-Disposition`, with anything that could steer a
 * path or a second header removed.
 *
 * The value is the **service's**, not a client's — fleet-billing-svc composes
 * `mageride-invoice-{yyyy-MM}-{invoiceId}` from ids it minted — so this is not a
 * trust boundary so much as the same rule the kernel applies to object-store keys:
 * never build a name out of bytes that arrived over a wire. A quote, a separator
 * or a control character is dropped rather than the whole name being refused,
 * because a download that failed over its own filename would be a worse outcome
 * than one named a little differently.
 */
function filenameFrom(header: string | null): string | null {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(header ?? '');
  if (!match) return null;

  const cleaned = match[1]!
    .trim()
    // Control characters, quotes and both separators. A CR or an LF in a header
    // value is how a second header gets written by a string that was never one.
    // eslint-disable-next-line no-control-regex -- that is the point of the class.
    .replaceAll(/[\u0000-\u001F\u007F"\\/]/g, '')
    .replace(/^\.+/, '');

  return cleaned === '' ? null : cleaned;
}

/** Whether a body is already an encoded request body rather than a value to serialise. */
function isMultipart(body: unknown): body is FormData {
  return typeof FormData !== 'undefined' && body instanceof FormData;
}

function encodeBody(body: unknown): BodyInit | undefined {
  if (body === undefined) return undefined;
  return isMultipart(body) ? body : JSON.stringify(body);
}
