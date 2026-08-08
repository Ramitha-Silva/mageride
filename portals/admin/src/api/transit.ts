/**
 * SCR-AP-016's wire shapes — the GTFS Dataset Manager's versioned feed lifecycle
 * (AL-54, US-28.1…28.3).
 *
 * Transcribed from `backend/contracts/transit.yaml`, and **not** from
 * `admin-bff.yaml`, because the lifecycle is transit-svc's: it owns the upload,
 * the validation job, the staging load and the one transaction that renames a
 * schema into place. admin-bff carries a pass-through copy of the same six routes
 * (`GtfsProxyEndpoints`) which `gateway-routes.json` shadows at Order 20 — so the
 * *portal* calls one set of paths and does not care which process answers them.
 *
 * ## Three things about this surface that shape every screen below it
 *
 * **The feed is somebody else's file (AL-56).** There is no authoring route here
 * and there must never be one: the day-0 national feed and every refresh arrive as
 * a finished zip, and server-side validation (BR-32.1) is the only quality gate
 * MageRide applies. The screen's whole vocabulary is upload / validate / activate /
 * roll back.
 *
 * **A duplicate is refused on the bytes, not on a header.** `POST …/uploads`
 * dedupes on the file's own sha256, so a retry that regenerated its
 * `Idempotency-Key` and the same file uploaded a month later by a different
 * operator are both `409 feed-duplicate` — carrying the version that already holds
 * those bytes, which is what {@link duplicateFeed} reads and what SCR-AP-016's
 * inline error names.
 *
 * **Activation is atomic and rollback is the same call.** `POST …/{id}/activate`
 * on an *archived, validated* version is BR-32.3's one-click rollback: same route,
 * same single-transaction swap, same guarantee that a failure leaves the feed that
 * is live now still live.
 */

import type { CursorPage } from './types';
import type { ProblemDetails } from './problem';

/* ---------------------------------------------------------------------------
 * Paths
 *
 * Two string literals, because `test/fences.test.ts` enumerates every `/v1/**`
 * this portal names and the set is meant to grow one deliberate line at a time.
 * Everything else below is composed from them.
 * ------------------------------------------------------------------------ */

/** `POST` a GTFS zip for validation · the id-addressed status, report and activate sit under it. */
export const GTFS_UPLOADS_PATH = '/v1/admin/transit/gtfs/uploads';

/** `GET` the feed version history, newest first (US-28.3). */
export const GTFS_VERSIONS_PATH = '/v1/admin/transit/gtfs/versions';

/** `GET …/uploads/{id}` — validation status and preview; SCR-AP-016 polls it every 2 s. */
export function gtfsUploadPath(feedVersionId: string): string {
  return `${GTFS_UPLOADS_PATH}/${feedVersionId}`;
}

/** `GET …/uploads/{id}/report` — every error and warning with its file, row and code. */
export function gtfsReportPath(feedVersionId: string): string {
  return `${GTFS_UPLOADS_PATH}/${feedVersionId}/report`;
}

/** `POST …/uploads/{id}/activate` — the atomic swap, and BR-32.3's rollback. */
export function gtfsActivatePath(feedVersionId: string): string {
  return `${GTFS_UPLOADS_PATH}/${feedVersionId}/activate`;
}

/** `GET …/versions/{id}/download` — 302 to a short-lived signed URL for the original zip. */
export function gtfsDownloadPath(feedVersionId: string): string {
  return `${GTFS_VERSIONS_PATH}/${feedVersionId}/download`;
}

/* ---------------------------------------------------------------------------
 * Wire shapes
 * ------------------------------------------------------------------------ */

/**
 * `transit.yaml#/components/schemas/FeedStatus` — the `gtfs_feed_versions.status`
 * CHECK, exactly.
 *
 * Six values and **exactly one row is ever `active`**, held by a partial unique
 * index rather than by anything here. `validated` and `archived` are the two a
 * feed can be activated *from*; `uploaded` and `validating` have no verdict yet
 * and `failed` can never be made live.
 */
export type FeedStatus =
  | 'uploaded'
  | 'validating'
  | 'validated'
  | 'failed'
  | 'active'
  | 'archived';

/**
 * Per-file row counts, keyed by the GTFS **file** name — `stop_times`, not
 * `stopTimes`.
 *
 * The keys are data rather than property names and transit-svc serialises them
 * literally for exactly that reason, so this is an index signature and not a
 * fixed record: a feed that carries `calendar_dates` and no `calendar` produces a
 * different key set from one that carries both, and the preview grid renders
 * whatever arrived.
 */
export type FeedCounts = Readonly<Record<string, number>>;

/** `GET …/uploads/{feedVersionId}` — what the status stepper and the preview card read. */
export interface FeedUploadStatus {
  readonly feedVersionId: string;
  readonly status: FeedStatus;
  readonly counts?: FeedCounts;
  /** The `feed_info.txt` version string. Absent for a feed that omits the file. */
  readonly feedInfoVersion?: string | null;
  /**
   * The service window, read **out of the feed** rather than derived in
   * Asia/Colombo — so unlike every other business date on the platform it carries
   * no `tzAt` companion, and `formatBusinessDate` (UTC-pinned) is the right
   * formatter for it.
   */
  readonly serviceStart?: string | null;
  readonly serviceEnd?: string | null;
  readonly warnings?: readonly string[];
  /**
   * **At most five.** The full row-level list is a separate download, because a
   * feed whose `stop_times.txt` names a missing stop is wrong on every one of half
   * a million rows and this response has to stay usable.
   */
  readonly errorSummary?: readonly string[];
}

/** One row of the version-history table, and the 200 of activate. */
export interface FeedVersion {
  readonly feedVersionId: string;
  readonly feedInfoVersion?: string | null;
  readonly fileName: string;
  readonly sha256?: string;
  /**
   * The operator's **user id**, not their name.
   *
   * The wireframe prints `admin@mageride.lk`; nothing on the platform can produce
   * it. iam-svc exposes no route that lists or resolves an internal user (C108
   * found the same gap on SCR-AP-008), so the id is shown as the id — it is what
   * an auditor matches against `audit.events.actor_id` anyway. Recorded in the
   * C110 handoff.
   */
  readonly uploadedBy: string;
  readonly uploadedAt: string;
  readonly counts?: FeedCounts;
  readonly status: FeedStatus;
  readonly activatedAt?: string | null;
  readonly archivedAt?: string | null;
}

export type FeedVersionPage = CursorPage<FeedVersion>;

/** `POST …/uploads` answers `202` with nothing but the id it minted. */
export interface FeedUploadAccepted {
  readonly feedVersionId: string;
}

/* ---------------------------------------------------------------------------
 * BR-32.1's ceiling, and the shape of what may be uploaded
 * ------------------------------------------------------------------------ */

/** 200 MB (BR-32.1, D2 "≤ 200 MB", `Transit:Gtfs:MaxUploadBytes`). */
export const MAX_FEED_BYTES = 200 * 1024 * 1024;

/** The one accepted extension. A GTFS feed is a zip and nothing else is one. */
export const FEED_ACCEPT = '.zip';

/**
 * Room for the multipart envelope on top of the file itself.
 *
 * The relay refuses a declared `Content-Length` over this before it opens a
 * connection upstream, matching `GtfsAdminEndpoints.RequireWithinLimit` — which
 * uses the same allowance for the same reason. Refusing at exactly 200 MB would
 * reject a 200 MB feed for the boundary strings wrapped around it.
 */
export const MULTIPART_OVERHEAD_BYTES = 1024 * 1024;

/**
 * `GET …/uploads/{id}/report` in either of the two forms the contract offers.
 *
 * CSV is the default the screen offers first — "what an operator actually fixes
 * the feed from", in D3's own words — and JSON is beside it because the report is
 * also the thing somebody diffs between two uploads.
 */
export type ReportFormat = 'csv' | 'json';

export function isReportFormat(value: string | null | undefined): value is ReportFormat {
  return value === 'csv' || value === 'json';
}

/** What the report route asks for, and what it labels the file it relays. */
export const REPORT_MEDIA_TYPES: Readonly<Record<ReportFormat, string>> = {
  csv: 'text/csv',
  json: 'application/json',
};

/* ---------------------------------------------------------------------------
 * Shapes checked before an id reaches a path this process builds
 * ------------------------------------------------------------------------ */

/**
 * transit-svc routes every id-addressed GTFS route on `{feedVersionId:guid}`, so
 * anything else is a 404 there whatever this does. Checking the shape here means
 * the refusal never *depends* on that — the same rule C106 applies to a subject id
 * and C109 to a `?id=`: a value that cannot be an id asks the platform nothing.
 */
const FEED_VERSION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isFeedVersionId(value: string | undefined | null): value is string {
  return typeof value === 'string' && FEED_VERSION_ID.test(value);
}

/* ---------------------------------------------------------------------------
 * The duplicate refusal, which carries more than a status
 * ------------------------------------------------------------------------ */

/** What `409 feed-duplicate` names beside the refusal. */
export interface DuplicateFeed {
  readonly feedVersionId: string;
  readonly feedInfoVersion: string | null;
}

/**
 * The version that already holds these bytes, out of a `409 feed-duplicate`.
 *
 * `GtfsUploadService.RejectDuplicateAsync` attaches the existing version as
 * problem extensions and says why in as many words: "a bare 409 leaves the
 * operator with a message and nowhere to go". RFC 7807 extensions are not on
 * {@link ProblemDetails} — they are per-error, and a portal-wide type carrying
 * every service's would be one nobody could read — so they are narrowed here, at
 * the one screen with a use for them.
 *
 * **The third extension, `status`, is deliberately not read.** RFC 7807 already
 * has a `status` member and it is the HTTP one, so the feed status is written
 * over the top of it in the same object; `readProblem` puts the transport's 409
 * back, which is the right answer for the field the portal shows. The feed status
 * is therefore unreachable from here — and unnecessary, because the link this
 * builds opens the version and the screen states its status in full. Raised in the
 * C110 handoff.
 *
 * `null` for anything that is not that refusal, so a caller falls back to the
 * generic sentence rather than rendering "(version undefined)".
 */
export function duplicateFeed(problem: ProblemDetails): DuplicateFeed | null {
  const extensions = problem as ProblemDetails & Readonly<Record<string, unknown>>;

  const id = extensions['feedVersionId'];
  const feedVersionId = typeof id === 'string' ? id : undefined;
  if (!isFeedVersionId(feedVersionId)) return null;

  const version = extensions['feedInfoVersion'];

  return {
    feedVersionId,
    feedInfoVersion: typeof version === 'string' && version.trim() ? version.trim() : null,
  };
}

/* ---------------------------------------------------------------------------
 * The lifecycle, as questions the screen asks of a row
 * ------------------------------------------------------------------------ */

/**
 * Whether this version is still being judged, and therefore whether the screen
 * should keep polling (D2: "poll 2 s").
 */
export function isPending(status: FeedStatus): boolean {
  return status === 'uploaded' || status === 'validating';
}

/**
 * Whether this version can be made live.
 *
 * `validated` is a first activation; `archived` is BR-32.3's rollback, which is
 * the *same* call. Everything else is refused, and refused by transit-svc too —
 * `409 feed-not-validated` for a feed that never passed and
 * `409 feed-already-active` for the one that is already live. Drawing the button
 * only where it can work is a courtesy; the 409 is the rule.
 */
export function isActivatable(status: FeedStatus): boolean {
  return status === 'validated' || status === 'archived';
}

/** Whether making this version live is a rollback rather than a first activation. */
export function isRollback(status: FeedStatus): boolean {
  return status === 'archived';
}

/**
 * How a feed version is named on screen.
 *
 * The `feed_info.txt` version where the feed carries one — which is what the
 * wireframe's `feed-20260722` is, and what an operator recognises. A feed that
 * omits the optional file has no such name, and the id is then the only honest
 * label: inventing "version 4" from a row number would be a name that changes the
 * moment somebody uploads something older.
 */
export function feedLabel(version: Pick<FeedVersion, 'feedVersionId' | 'feedInfoVersion'>): string {
  return version.feedInfoVersion?.trim() ? version.feedInfoVersion.trim() : version.feedVersionId;
}
