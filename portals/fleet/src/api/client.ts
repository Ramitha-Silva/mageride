import 'server-only';

import { randomUUID } from 'node:crypto';

import { canMutate } from '@/server/access';
import { accessToken, getSession } from '@/server/session';

import { apiFetch } from './http';
import { localProblem, ProblemError } from './problem';

/**
 * The typed data layer every Fleet Portal screen calls the platform through.
 *
 * Two functions, and two fences neither of them lets a screen round.
 *
 * ## 1. Every read is org-scoped, and the org is never the screen's to choose
 *
 * A screen asks for `{ org: '/vehicles' }` and gets
 * `/v1/fleets/{the caller's own fleetId}/vehicles`. The id comes from
 * `GET /v1/me/permissions` — iam-svc's reading of `iam.fleet_members` — and there
 * is no parameter, no prop and no query string by which a screen can name a
 * different one. That is the component fence "a cross-org read must be impossible
 * from the client" as code rather than as a rule: not "screens are careful with
 * the id" but "screens never hold one".
 *
 * The server refuses anyway, and that is the part that matters (`FleetAccessFilter`
 * answers `403 not-fleet-member`, and C058's row-level security refuses beneath
 * it). This side makes the attempt unrepresentable.
 *
 * ## 2. Every mutation declares the URD §2.3 row it needs, and is checked here
 *
 * `mutate()` takes a `requires` declaration and refuses locally when the caller's
 * own evaluated permissions do not carry `write` in that row. A Viewer's rows
 * never do — `PermissionEvaluator` restricts the whole `fleet_owner` column to
 * `Read | OwnScope` for them — so "a Viewer session renders no mutating control
 * anywhere" has a second half: if one is ever rendered by mistake, pressing it
 * changes nothing. `test/fences.test.ts` fails the build on a `mutate(` with no
 * declaration.
 */

/** Where a call is addressed: inside the caller's organisation, or absolutely. */
export type ApiTarget =
  /** A path **inside the caller's own organisation** — `/vehicles`, `/map`, `/members`. */
  | { readonly org: string }
  /**
   * An absolute `/v1/...` path, for the routes that are not org-scoped. In the
   * shell that is iam-svc's `/v1/me/permissions` and the three auth routes; a
   * screen that reaches for this is asking to leave the organisation and has to
   * say so in the diff.
   */
  | { readonly path: string };

export type ReadOptions = ApiTarget & {
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
};

/**
 * A GET as the signed-in member of the caller's organisation.
 *
 * A caller with no session gets a `401 unauthorized` problem rather than an
 * anonymous request: this portal has no anonymous reads, and sending one would
 * turn a signed-out tab into a 403 from the API that reads like a permissions bug.
 */
export async function read<T>(options: ReadOptions): Promise<T> {
  const path = await resolvePath(options);
  const token = await requireToken(path);

  const { data } = await apiFetch<T>({
    path,
    accessToken: token,
    ...(options.searchParams ? { searchParams: options.searchParams } : {}),
    ...(options.signal ? { signal: options.signal } : {}),
  });
  return data;
}

/**
 * The URD §2.3 row a mutation needs, declared by the screen that makes it.
 *
 * Required, and deliberately not defaulted: "this call forgot to say what it
 * needs" and "this call needs nothing" must not be the same value, because the
 * second is never true — every write on this portal is gated on something.
 */
export interface RequiredGrant {
  /** The row, e.g. `fleet-operations`. `write` is the capability, always. */
  readonly area: string;
  /**
   * Whether the route sits inside a group carrying `RequireApprovedFleet()`.
   * Set it where the endpoint sets it, and nowhere else — a portal that guessed
   * high would refuse writes US-13.A7 allows a pending organisation to make, and
   * the payout profile is exactly such a write (AL-49: the documents are part of
   * what the Verification Officer reads *before* approving).
   */
  readonly requiresApprovedOrg?: boolean;
  /**
   * Whether the route works for a caller who belongs to no organisation yet.
   * **True for `POST /v1/fleets` and nothing else** — see {@link canMutate}.
   */
  readonly allowsNoOrganisation?: boolean;
}

export type MutateOptions<TBody = unknown> = ApiTarget & {
  readonly method: 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  readonly body?: TBody;
  readonly requires: RequiredGrant;
  /**
   * R-14/R-18 replay key. Defaults to a fresh UUID.
   *
   * Pass a **stable** one for an action a double-click must not perform twice and
   * whose server-side key is not already the business fact.
   */
  readonly idempotencyKey?: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
};

export interface MutationOutcome<T> {
  readonly data: T;
  readonly status: number;
  /** The key that was sent, so a retry of *this* attempt can reuse it. */
  readonly idempotencyKey: string;
}

/**
 * A mutation, carrying its `Idempotency-Key` and its capability declaration.
 *
 * The header is on every write because `_shared.yaml` marks the parameter
 * `required: true` and fleet-svc's command log keys on it (R-14): replaying a key
 * with an identical payload replays the original response, and replaying it with a
 * different one is `409 idempotency-key-reuse`. Without it an impatient operator's
 * second click is a second vehicle.
 */
export async function mutate<T = unknown, TBody = unknown>(
  options: MutateOptions<TBody>,
): Promise<MutationOutcome<T>> {
  const path = await resolvePath(options);
  const session = await getSession();

  // The local half of "a Viewer renders no mutating control". The server decides
  // — `FleetAccessFilter` re-reads the seat and every endpoint re-checks the row —
  // and this refusal exists so that a control drawn by mistake changes nothing.
  if (!session || !canMutate(session, options.requires.area, options.requires)) {
    throw new ProblemError(
      localProblem(
        'fleet-role-insufficient',
        403,
        path,
        `This session does not hold write on "${options.requires.area}".`,
      ),
    );
  }

  const token = await requireToken(path);
  const idempotencyKey = options.idempotencyKey ?? randomUUID();

  const { data, status } = await apiFetch<T>({
    path,
    method: options.method,
    accessToken: token,
    idempotencyKey,
    ...(options.body === undefined ? {} : { body: options.body }),
    ...(options.searchParams ? { searchParams: options.searchParams } : {}),
    ...(options.signal ? { signal: options.signal } : {}),
  });

  return { data, status, idempotencyKey };
}

/* ------------------------------------------------------------------------- */

/**
 * Turns a target into the one path this process will actually call.
 *
 * **This is the only place in the portal that writes a `{fleetId}` into a URL**,
 * and the id it writes is the session's own. `test/fences.test.ts` enumerates the
 * tree and fails on a second one.
 */
async function resolvePath(target: ApiTarget): Promise<string> {
  if ('path' in target) {
    if (!target.path.startsWith('/v1/')) {
      throw new TypeError(`An absolute API path must start with /v1/, got "${target.path}".`);
    }
    return target.path;
  }

  const session = await getSession();
  if (!session?.fleetId) {
    // Not an error state anybody navigated into: `./access` gives an account with
    // no membership exactly one reachable screen, and it is the one that creates
    // the organisation. Reaching here means a screen asked for org-scoped data
    // before there was an org.
    throw new ProblemError(
      localProblem(
        'not-fleet-member',
        403,
        '/v1/fleets',
        'This account holds no fleet membership, so there is no organisation to read.',
      ),
    );
  }

  if (!target.org.startsWith('/')) {
    throw new TypeError(`An org-scoped path must start with "/", got "${target.org}".`);
  }

  return `/v1/fleets/${session.fleetId}${target.org === '/' ? '' : target.org}`;
}

async function requireToken(path: string): Promise<string> {
  const token = await accessToken();
  if (!token) {
    throw new ProblemError(
      localProblem('unauthorized', 401, path, 'The Fleet Portal session has ended.'),
    );
  }
  return token;
}
