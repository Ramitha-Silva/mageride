/**
 * RFC 7807 `application/problem+json`, which D3' §0 makes the shape of **every**
 * MageRide error, and how the portal turns one into something a Sinhala-reading
 * operator can act on.
 *
 * The contract is explicit that `title` is "Short English summary for developers.
 * **Never localised** — user-facing copy is resolved from the code by the client's
 * Si/Ta/En resources." So the rule here is: the *code* is the message, the `detail`
 * is diagnostics. A portal that rendered `problem.title` would be a portal that
 * shows English to every user in the country, one error at a time — and it would
 * pass every review, because the string is not in the source.
 */

import type { AdminMessageKey } from '@/i18n';

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
  /** `423 otp-locked` carries this (AL-37's lock-out). */
  readonly retryAfterSeconds?: number;
}

const ERROR_TYPE_PREFIX = 'https://mageride.lk/errors/';

/**
 * A failed API call, carrying the problem rather than a string.
 *
 * Deliberately not a subclass per status: every caller branches on `code`, which is
 * the stable kebab registry D3' §0 says "a client can branch on the code alone".
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

  /** The i18n key this error renders as. */
  get messageKey(): AdminMessageKey {
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
 * Every error code the shell can put in front of somebody, mapped to its resource
 * key. A code with no entry falls back to `error.unexpected` **plus the trace id**,
 * which is what support asks for — deliberately not the English `title`.
 *
 * The list is short because the shell only issues five kinds of request: sign in,
 * refresh, sign out, read the session, and whatever a screen component asks for
 * through {@link ../api/client}. Screen components extend their own copy, in all
 * three locale files, in the same change.
 */
const MESSAGE_KEYS: Readonly<Record<string, AdminMessageKey>> = {
  unauthorized: 'admin.error.unauthorized',
  forbidden: 'admin.error.forbidden',
  'not-found': 'admin.error.notFound',
  'validation-failed': 'admin.error.validationFailed',
  'bad-request': 'admin.error.validationFailed',
  conflict: 'admin.error.conflict',
  'user-blocked': 'admin.error.accountBlocked',
  'auth-not-found': 'admin.error.invalidCredentials',
  'otp-locked': 'admin.error.accountLocked',
  'rate-limited': 'admin.error.rateLimited',
  'dependency-unavailable': 'admin.error.serviceUnavailable',
  'service-unavailable': 'admin.error.serviceUnavailable',
  'upstream-timeout': 'admin.error.serviceUnavailable',
  'internal-error': 'admin.error.unexpected',
  // Δ C110 · SCR-AP-016. The GTFS lifecycle's four refusals, which the generic
  // `conflict` sentence ("someone changed this first — reload") describes wrongly
  // in every case: a duplicate feed, a feed that never passed validation and the
  // feed that is already live are three different things an operator does three
  // different things about. Added here rather than branched on in the screen so
  // `ProblemPanel` and the two server actions render the same sentence.
  'feed-duplicate': 'admin.error.feedDuplicate',
  'feed-not-validated': 'admin.error.feedNotValidated',
  'feed-already-active': 'admin.error.feedAlreadyActive',
  'payload-too-large': 'admin.error.payloadTooLarge',
};

export function problemMessageKey(problem: ProblemDetails): AdminMessageKey {
  return MESSAGE_KEYS[errorCode(problem)] ?? 'admin.error.unexpected';
}

/**
 * Reads a problem out of a response, and invents one when the body is not a
 * problem at all.
 *
 * A gateway 502 with an HTML body, or a socket that closed mid-response, is not
 * something the services produced — but it is still an error the operator has to
 * be told about, and the alternative to synthesising a problem here is a
 * `SyntaxError: Unexpected token '<'` on somebody's screen.
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
      // decide how the portal reacts.
      status: response.status,
      type: typeof parsed.type === 'string' ? parsed.type : fallback.type,
      title: typeof parsed.title === 'string' ? parsed.title : fallback.title,
      instance: typeof parsed.instance === 'string' ? parsed.instance : instance,
    };
  } catch {
    return fallback;
  }
}

/** A problem for a failure that never reached a service at all. */
export function localProblem(code: string, status: number, instance: string, detail?: string): ProblemDetails {
  return {
    type: `${ERROR_TYPE_PREFIX}${code}`,
    title: code,
    status,
    instance,
    detail,
  };
}
