import 'server-only';

import { randomUUID } from 'node:crypto';

import { accessToken } from '@/server/session';

import type { AuditIntent } from './audit';
import { apiDownload, apiFetch, type ApiDownload } from './http';
import { localProblem, ProblemError } from './problem';

/**
 * The typed data layer every Admin Portal screen calls admin-bff through.
 *
 * Two functions, and the split between them is the point: {@link read} for the
 * reads, {@link mutate} for everything that changes something. A screen that
 * reaches for `fetch` directly has left both the RBAC surface and the D-35
 * surface, and `test/data-layer.test.ts` enumerates the tree to make sure none
 * does.
 */

export interface ReadOptions {
  /** An absolute API path, `/v1/admin/...`. */
  readonly path: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
}

/**
 * A GET against admin-bff, as the signed-in operator.
 *
 * A caller with no session gets a `401 unauthorized` problem rather than an
 * anonymous request: an operator console has no anonymous reads, and sending one
 * would turn a signed-out tab into a 403 from the API that reads like a
 * permissions bug.
 */
export async function read<T>(options: ReadOptions): Promise<T> {
  const token = await requireToken(options.path);

  const { data } = await apiFetch<T>({
    path: options.path,
    accessToken: token,
    ...(options.searchParams ? { searchParams: options.searchParams } : {}),
    ...(options.signal ? { signal: options.signal } : {}),
  });
  return data;
}

export interface DownloadOptions {
  /** An absolute API path, `/v1/admin/...`. */
  readonly path: string;
  /** The media type the route answers — `text/csv` for AL-38's export. */
  readonly accept: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
}

/**
 * A GET whose answer is a file, as the signed-in operator.
 *
 * The third member of the data layer, and it is a `read` in every sense that
 * matters — same bearer, same refusal with no session, same problem out. It is
 * separate only because the body is bytes: an export is relayed from the service
 * that computed it, never re-rendered here (see `apiDownload`).
 *
 * A download is **not** a mutation and takes no {@link AuditIntent}: D-35 audits
 * changes, and AL-39/AL-40 add `DOC_VIEW` and `PII_READ` for the reads that
 * disclose a person's data. A count of last month's trips discloses nothing about
 * anybody — admin-bff's own dashboard endpoints say so in as many words — so a row
 * here would bury the rows that matter.
 */
export async function download(options: DownloadOptions): Promise<ApiDownload> {
  const token = await requireToken(options.path);

  return apiDownload({
    path: options.path,
    accept: options.accept,
    accessToken: token,
    ...(options.searchParams ? { searchParams: options.searchParams } : {}),
    ...(options.signal ? { signal: options.signal } : {}),
  });
}

export interface MutateOptions<TBody = unknown> {
  readonly method: 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  readonly path: string;
  readonly body?: TBody;
  /**
   * The `audit.events` row this call is going to cause (D-35). **Required.**
   *
   * Not because the portal writes it — admin-bff's interceptor does, and refuses
   * to start if a mutating route could avoid it — but because a screen that cannot
   * name the row it causes cannot tell the operator what is about to be recorded
   * against their name, and this console suspends drivers and reverses fees.
   */
  readonly audit: AuditIntent;
  /**
   * R-14/R-18 replay key. Defaults to a fresh UUID.
   *
   * Pass a **stable** one for an action a double-click must not perform twice and
   * whose server-side key is not already the business fact — the fee reversal, for
   * instance, keys its ledger entry on `{driver}:{vehicle}:{date}` and needs
   * nothing from here (C065). Where the server has no such key, this is the only
   * thing standing between an impatient operator and two suspensions.
   */
  readonly idempotencyKey?: string;
  readonly searchParams?: Readonly<Record<string, string | number | boolean | undefined>>;
  readonly signal?: AbortSignal;
}

export interface MutationOutcome<T> {
  readonly data: T;
  readonly status: number;
  /**
   * The row the platform recorded, echoed back so a success toast can say what was
   * written down without re-deriving it.
   */
  readonly audit: AuditIntent;
  /** The key that was sent, so a retry of *this* attempt can reuse it. */
  readonly idempotencyKey: string;
}

/**
 * A mutation against admin-bff, carrying its `Idempotency-Key` and declaring its
 * D-35 row.
 *
 * **This helper does not write the audit row and must never try to.** The row is
 * written by admin-bff's interceptor inside the same transaction as the change
 * (C062: "the change and its audit row commit together or not at all"), and a
 * portal that also posted one would produce a second, unbacked entry in an
 * immutable log — an entry claiming an action happened when the transaction it
 * belonged to may have rolled back. What the portal contributes is the
 * `Idempotency-Key`, the operator's bearer, and the declaration above.
 */
export async function mutate<T = unknown, TBody = unknown>(
  options: MutateOptions<TBody>,
): Promise<MutationOutcome<T>> {
  const token = await requireToken(options.path);
  const idempotencyKey = options.idempotencyKey ?? randomUUID();

  const { data, status } = await apiFetch<T>({
    path: options.path,
    method: options.method,
    accessToken: token,
    idempotencyKey,
    ...(options.body === undefined ? {} : { body: options.body }),
    ...(options.searchParams ? { searchParams: options.searchParams } : {}),
    ...(options.signal ? { signal: options.signal } : {}),
  });

  return { data, status, audit: options.audit, idempotencyKey };
}

async function requireToken(path: string): Promise<string> {
  const token = await accessToken();
  if (!token) {
    throw new ProblemError(
      localProblem('unauthorized', 401, path, 'The Admin Portal session has ended.'),
    );
  }
  return token;
}
