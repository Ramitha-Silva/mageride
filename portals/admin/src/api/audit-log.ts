/**
 * SCR-AP-009's wire shape and its filters — `GET /v1/admin/audit-log` (US-19.3,
 * D-35).
 *
 * Separate from `./audit.ts`, which is the **vocabulary a mutation declares**.
 * This is the **log a reader reads**, and the two have opposite directions: one is
 * checked against `AdminAuditActions.cs` because the portal must only ever name
 * rows admin-bff can write, the other must render whatever is in the table —
 * including actions written by services that are not admin-bff, and including
 * actions added after this build shipped. A screen that only rendered the union it
 * knows would silently drop exactly the rows an auditor came looking for.
 *
 * ## Append-only, and there is no write route to leave out
 *
 * The contract says so in one line: "Append-only — there is no write route here."
 * So the Auditor's read-only requirement (US-21.7) is not something this screen
 * enforces by hiding controls — there is no control on the platform to hide. What
 * the screen must not do is grow one, and `test/audit-screen.test.tsx` asserts the
 * tree under `/audit-log` calls no mutation.
 *
 * ## `before` / `after` are what changed; `detail` is how it was asked for
 *
 * Δ C062 put both on the row because D-35's deliverable is "actor, action, target,
 * **before/after**, ip" and the schema had nowhere for the pair. They are kept
 * apart deliberately — `before`/`after` are the handler's knowledge of the
 * **entity**, `detail` is the interceptor's knowledge of the **request** (method,
 * path, the caller's whole role union, the idempotency key). One field holding
 * both would make "what changed" unreadable.
 */

import type { CursorPage, Role } from './types';

export type { CursorPage };

/** `GET /v1/admin/audit-log`. */
export const AUDIT_LOG_PATH = '/v1/admin/audit-log';

/** The contract's maximum page size. */
export const AUDIT_PAGE_SIZE = 100;

/**
 * How many pages the CSV export will follow before it stops.
 *
 * The export exists because US-19.3 asks for one and no route serves it (see
 * `app/(portal)/audit-log/export/route.ts`); a cursor-paged source has no natural
 * end, so the cap is stated here, stated in the file's own preamble, and stated on
 * the screen beside the link. A truncation nobody is told about would be the one
 * failure an audit export cannot have.
 */
export const AUDIT_EXPORT_MAX_PAGES = 20;

/** One row of the immutable log. */
export interface AuditEvent {
  readonly eventId: string;
  readonly actorId: string;
  readonly actorRole?: Role;
  /** Free text on the wire — `PII_READ`, `WALLET_FEE_REVERSED`, and whatever is added next. */
  readonly action: string;
  readonly subjectId?: string;
  readonly subjectType?: string;
  /** State before, or `null` where nothing existed — a creation, or a read event. */
  readonly before?: Record<string, unknown> | null;
  /** State after, or the observation itself. */
  readonly after?: Record<string, unknown> | null;
  /** What the interceptor knew about the request. */
  readonly detail?: Record<string, unknown>;
  readonly ip?: string;
  readonly occurredAt: string;
}

const ADMIN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isAdminId(value: string | undefined | null): value is string {
  return typeof value === 'string' && ADMIN_ID.test(value);
}

/** `action` is `maxLength: 60` and screaming snake — `AdminAuditActions.cs`'s own rule. */
const ACTION = /^[A-Z][A-Z0-9_]{0,59}$/;

export function isAuditAction(value: string | undefined): value is string {
  return value !== undefined && ACTION.test(value);
}

/**
 * An RFC 3339 instant, which is what `from`/`to` are on this route.
 *
 * **Not a `BusinessDate`** — the audit filters are `Timestamp`, unlike every other
 * date filter on this surface, because a day of admin actions is a window on a
 * clock rather than a business day. The form takes `datetime-local` and this is
 * what accepts what it produces, seconds optional.
 */
const INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?(Z|[+-]\d{2}:\d{2})?$/;

export function isInstant(value: string | undefined): value is string {
  if (!value || !INSTANT.test(value)) return false;
  return !Number.isNaN(new Date(value).getTime());
}

export interface AuditSelection {
  readonly actorId?: string;
  readonly action?: string;
  readonly subjectId?: string;
  readonly from?: string;
  readonly to?: string;
  readonly cursor?: string;
  /** What was typed into the two id boxes, so a malformed value is kept and explained. */
  readonly typedActor: string;
  readonly typedSubject: string;
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

export function auditSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): AuditSelection {
  const actorId = (first(params.actorId) ?? '').trim();
  const subjectId = (first(params.subjectId) ?? '').trim();
  const action = (first(params.action) ?? '').trim().toUpperCase();
  const from = first(params.from);
  const to = first(params.to);
  const cursor = first(params.cursor);

  return {
    typedActor: actorId,
    typedSubject: subjectId,
    ...(isAdminId(actorId) ? { actorId } : {}),
    ...(isAdminId(subjectId) ? { subjectId } : {}),
    ...(isAuditAction(action) ? { action } : {}),
    ...(isInstant(from) ? { from } : {}),
    ...(isInstant(to) ? { to } : {}),
    ...(cursor ? { cursor } : {}),
  };
}

/**
 * The selection as the route's query — for the screen, for the next page and for
 * the export.
 *
 * One function, so "the CSV contains exactly the rows the filter is showing" is
 * one query asked twice rather than two queries that agree today.
 */
export function auditSearch(
  selection: AuditSelection,
  overrides: { readonly cursor?: string | null } = {},
): Record<string, string | number> {
  const cursor = 'cursor' in overrides ? overrides.cursor : selection.cursor;

  return {
    ...(selection.actorId ? { actorId: selection.actorId } : {}),
    ...(selection.action ? { action: selection.action } : {}),
    ...(selection.subjectId ? { subjectId: selection.subjectId } : {}),
    ...(selection.from ? { from: selection.from } : {}),
    ...(selection.to ? { to: selection.to } : {}),
    ...(cursor ? { cursor } : {}),
    limit: AUDIT_PAGE_SIZE,
  };
}

/** Whether anything is narrowing the log — what decides if "Clear" is drawn. */
export function isFiltered(selection: AuditSelection): boolean {
  return Boolean(
    selection.actorId ?? selection.action ?? selection.subjectId ?? selection.from ?? selection.to,
  );
}

/** `path` with this selection's filters on it. The cursor is passed explicitly or dropped. */
export function auditHref(
  path: string,
  selection: AuditSelection,
  overrides: { readonly cursor?: string | null } = {},
): string {
  const query = new URLSearchParams();
  const cursor = 'cursor' in overrides ? overrides.cursor : selection.cursor;

  if (selection.actorId) query.set('actorId', selection.actorId);
  if (selection.action) query.set('action', selection.action);
  if (selection.subjectId) query.set('subjectId', selection.subjectId);
  if (selection.from) query.set('from', selection.from);
  if (selection.to) query.set('to', selection.to);
  if (cursor) query.set('cursor', cursor);

  const search = query.toString();
  return search ? `${path}?${search}` : path;
}
