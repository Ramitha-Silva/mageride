/**
 * RFC 7807 `application/problem+json`, which D3' §0 makes the shape of **every**
 * MageRide error, and how the portal turns one into something a Sinhala-reading
 * fleet operator can act on.
 *
 * The contract is explicit that `title` is "Short English summary for developers.
 * **Never localised** — user-facing copy is resolved from the code by the client's
 * Si/Ta/En resources." So the rule here is: the *code* is the message, the `detail`
 * is diagnostics. A portal that rendered `problem.title` would show English to
 * every user in the country, one error at a time — and it would pass every review,
 * because the string is not in the source.
 */

import type { FleetMessageKey } from '@/i18n';

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
  /** `423 otp-locked` carries this (AL-37's lock-out, which both portals share). */
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
  get messageKey(): FleetMessageKey {
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
 * Every error code the **shell** can put in front of somebody, mapped to its
 * resource key. A code with no entry falls back to `fleet.error.unexpected` plus
 * the trace id, which is what support asks for — deliberately not the English
 * `title`.
 *
 * The list is short because the shell only issues six kinds of request: sign in
 * (three arms), refresh, sign out, read the caller's permissions and read the
 * organisation. Screen components extend their own copy, in all three locale
 * files, in the same change.
 *
 * The four fleet-identity codes are here rather than in a screen because they are
 * answers to the shell's own two reads: `FleetAccessFilter` refuses in that order
 * — 404 on the organisation, then 403 on the membership, then 403 on the sub-role,
 * then 403 on approval — and each of the four means something different about
 * what the operator should do next. The generic `forbidden` sentence describes
 * none of them.
 */
const MESSAGE_KEYS: Readonly<Record<string, FleetMessageKey>> = {
  unauthorized: 'fleet.error.unauthorized',
  forbidden: 'fleet.error.forbidden',
  'not-found': 'fleet.error.notFound',
  'validation-failed': 'fleet.error.validationFailed',
  'bad-request': 'fleet.error.validationFailed',
  conflict: 'fleet.error.conflict',
  'user-blocked': 'fleet.error.accountBlocked',
  'auth-not-found': 'fleet.error.invalidCredentials',
  'otp-locked': 'fleet.error.accountLocked',
  'rate-limited': 'fleet.error.rateLimited',
  'dependency-unavailable': 'fleet.error.serviceUnavailable',
  'service-unavailable': 'fleet.error.serviceUnavailable',
  'upstream-timeout': 'fleet.error.serviceUnavailable',
  'internal-error': 'fleet.error.unexpected',
  // The organisation family (fleet.yaml, FleetAccessFilter).
  'fleet-not-found': 'fleet.error.orgNotFound',
  'not-fleet-member': 'fleet.error.notMember',
  'fleet-role-insufficient': 'fleet.error.roleInsufficient',
  'fleet-not-approved': 'fleet.error.orgNotApproved',
  // Δ C112 — the codes SCR-FP-002 and SCR-FP-002a can be answered with. Each is
  // an `x-error-codes` entry on a route this component calls, and each says
  // something different about what the operator should do next: a duplicate
  // business registration is somebody else's application on the same number, a
  // duplicate member is a colleague who already has a seat, and an unsubmitted
  // payout profile is a screen to fill in rather than a failure.
  'business-registration-exists': 'fleet.error.registrationExists',
  'fleet-member-exists': 'fleet.error.memberExists',
  'payout-profile-not-found': 'fleet.error.payoutNotFound',
  'payout-profile-not-verified': 'fleet.error.payoutNotVerified',
  'payload-too-large': 'fleet.error.fileTooLarge',
  'unsupported-media-type': 'fleet.error.fileNotAccepted',
  // Δ C113 — the codes SCR-FP-004, SCR-FP-005 and SCR-FP-006 can be answered
  // with. Each is an `x-error-codes` entry on a route one of the three screens
  // calls, and each names a different thing for the operator to do: a duplicate
  // plate is somebody else's live registration, a duplicate IMEI is T-08's
  // anti-clone hold on **both** devices, and a bulk job already running is a
  // minute's wait rather than a failure.
  'registration-exists': 'fleet.error.vehicleRegistrationExists',
  'invalid-vehicle-type': 'fleet.error.invalidVehicleType',
  'mode-not-allowed': 'fleet.error.modeNotAllowed',
  'vehicle-not-found': 'fleet.error.vehicleNotFound',
  'driver-not-found': 'fleet.error.driverNotFound',
  'imei-duplicate': 'fleet.error.imeiDuplicate',
  'csv-invalid': 'fleet.error.csvInvalid',
  'too-many-rows': 'fleet.error.tooManyRows',
  'bulk-in-progress': 'fleet.error.bulkInProgress',
  'not-owner': 'fleet.error.notOwner',
  // The gateway's D-30 refusal, which reaches this portal on exactly one route —
  // see `bulkBindTrackers` in `src/server/tracker-actions.ts`.
  'attestation-failed': 'fleet.error.attestationFailed',
};

export function problemMessageKey(problem: ProblemDetails): FleetMessageKey {
  return MESSAGE_KEYS[errorCode(problem)] ?? 'fleet.error.unexpected';
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
