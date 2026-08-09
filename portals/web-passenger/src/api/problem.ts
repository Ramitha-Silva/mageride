/**
 * RFC 7807 `application/problem+json`, which D3' §0 makes the shape of **every**
 * MageRide error, and how this surface turns one into something a Sinhala- or
 * Tamil-reading recipient can act on.
 *
 * The contract is explicit that `title` is "Short English summary for developers.
 * **Never localised** — user-facing copy is resolved from the code by the client's
 * Si/Ta/En resources." So the rule here is the same one the other two portals
 * hold: the *code* is the message and the `detail` is diagnostics.
 *
 * `public-bff.yaml` makes the family's vocabulary short — `token-unknown`,
 * `token-expired-or-revoked`, `rate-limited`, `forbidden`, `receipt-not-ready`,
 * `validation-failed` — and two of those are not errors this surface *reports* at
 * all: a dead token is SCR-WT-006, which is a screen rather than a failure.
 */

import type { WebMessageKey } from '@/i18n/messages/en';

/** `_shared.yaml#/components/schemas/Problem`. */
export interface ProblemDetails {
  /** `https://mageride.lk/errors/{code}` where `{code}` is an `ErrorCode`. */
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail?: string;
  readonly instance?: string;
  /** W3C `traceparent` of the failing request — the one thing worth showing verbatim. */
  readonly traceId?: string;
  /** Field-level detail, present on `validation-failed`. */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  /** `429 rate-limited` carries this. */
  readonly retryAfterSeconds?: number;
}

const ERROR_TYPE_PREFIX = 'https://mageride.lk/errors/';

/**
 * A failed call to public-bff, carrying the problem rather than a string.
 *
 * Deliberately not a subclass per status: every caller branches on `code`, which
 * is the stable kebab registry D3' §0 says "a client can branch on the code alone".
 */
export class ProblemError extends Error {
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super(`${problem.status} ${problem.title}`);
    this.name = 'ProblemError';
    this.problem = problem;
  }

  /** The kebab `ErrorCode`, parsed out of the `type` URI. */
  get code(): string {
    return errorCode(this.problem);
  }

  get status(): number {
    return this.problem.status;
  }

  get messageKey(): WebMessageKey {
    return problemMessageKey(this.problem);
  }
}

/** The kebab `ErrorCode` a problem carries, or `unknown` when the URI is not one. */
export function errorCode(problem: ProblemDetails): string {
  if (problem.type?.startsWith(ERROR_TYPE_PREFIX)) {
    const code = problem.type.slice(ERROR_TYPE_PREFIX.length).replace(/\/+$/, '');
    if (code) return code;
  }
  return 'unknown';
}

/**
 * The two codes that mean **this link is over**, rather than **this request went
 * wrong**.
 *
 * `public-bff.yaml` makes them uniform across the whole family: `404 token-unknown`
 * for a token nobody minted and `410 token-expired-or-revoked` for one that has
 * been used, has timed out, or was closed when the trip ended. Both are
 * SCR-WT-006, and neither is an error the reader did anything about — which is
 * why they are a *screen* here and not a panel.
 *
 * Matched on the **status** as well as on the code, because a gateway or a proxy
 * that answered before public-bff did produces a 404 with a body that is not a
 * problem at all, and a dead end is the right destination for that too.
 */
export function isDeadToken(error: unknown): boolean {
  if (!(error instanceof ProblemError)) return false;

  return (
    error.status === 404 ||
    error.status === 410 ||
    error.code === 'token-unknown' ||
    error.code === 'token-expired-or-revoked'
  );
}

/**
 * Every code this surface can put in front of somebody, mapped to its resource
 * key. A code with no entry falls back to `web.error.unexpected` plus the trace
 * id, which is what support asks for — deliberately not the English `title`.
 *
 * The list is short because the surface issues five kinds of request, and the two
 * token codes are absent on purpose: they are handled by
 * {@link isDeadToken} before anything reaches a panel.
 */
const MESSAGE_KEYS: Readonly<Record<string, WebMessageKey>> = {
  'validation-failed': 'web.error.badLocation',
  'bad-request': 'web.error.badLocation',
  forbidden: 'web.error.forbidden',
  'rate-limited': 'web.error.rateLimited',
  'receipt-not-ready': 'web.error.receiptNotReady',
  conflict: 'web.error.receiptNotReady',
  'dependency-unavailable': 'web.error.serviceUnavailable',
  'service-unavailable': 'web.error.serviceUnavailable',
  'upstream-timeout': 'web.error.serviceUnavailable',
  'internal-error': 'web.error.unexpected',
};

export function problemMessageKey(problem: ProblemDetails): WebMessageKey {
  return MESSAGE_KEYS[errorCode(problem)] ?? 'web.error.unexpected';
}

/**
 * Reads a problem out of a response, and invents one when the body is not a
 * problem at all.
 *
 * A gateway 502 with an HTML body, or a socket that closed mid-response, is not
 * something the services produced — but it is still something the reader has to be
 * told about, and the alternative to synthesising a problem here is a
 * `SyntaxError: Unexpected token '<'` on somebody's phone.
 */
export async function readProblem(response: Response, instance: string): Promise<ProblemDetails> {
  const fallback: ProblemDetails = {
    type: `${ERROR_TYPE_PREFIX}${response.status >= 500 ? 'internal-error' : 'bad-request'}`,
    title: response.statusText || 'Request failed',
    status: response.status,
    instance,
  };

  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) return fallback;

  try {
    const body: unknown = await response.json();
    if (!body || typeof body !== 'object') return fallback;

    const parsed = body as Partial<ProblemDetails>;
    return {
      ...fallback,
      ...parsed,
      // The transport's status is the truth. A body that disagrees with its own
      // response line is a bug somewhere, and trusting the body would let it
      // decide whether this page becomes a dead end.
      status: response.status,
      type: typeof parsed.type === 'string' ? parsed.type : fallback.type,
      title: typeof parsed.title === 'string' ? parsed.title : fallback.title,
      instance: typeof parsed.instance === 'string' ? parsed.instance : instance,
    };
  } catch {
    return fallback;
  }
}

/** A problem for a failure that never reached public-bff at all. */
export function localProblem(
  code: string,
  status: number,
  instance: string,
  detail?: string,
): ProblemDetails {
  return {
    type: `${ERROR_TYPE_PREFIX}${code}`,
    title: code,
    status,
    instance,
    detail,
  };
}
