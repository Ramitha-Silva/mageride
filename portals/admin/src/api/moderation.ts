/**
 * SCR-AP-004's wire shapes, its two paths and the state the screen keeps in its
 * URL.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` — `listReportQueue`,
 * `resolveReport`, `suspendVehicle` and `suspendDriver`, the four operations
 * tagged `admin-moderation` that are not the ticket queue.
 *
 * ## What the queue is, and what it is not
 *
 * `GET /v1/admin/reports/queue` is safety-svc's **pending** vehicle-report inbox,
 * forwarded (C052 owns the rows, admin-bff owns the RBAC gate and the D-35 row).
 * Membership is decided there — a decided report leaves the queue because it is no
 * longer pending, not because this screen filters it out — so there is no status
 * filter here and there must not be one.
 *
 * ## The three-strike rule is stated here and counted there
 *
 * US-12.6: **the third confirmation delists the vehicle**, inside the same
 * transaction that writes the third status, so two moderators confirming at once
 * cannot both read two. That is why {@link ResolvedReport} carries
 * `confirmedCount` and `vehicleDelisted` and the queue row does not: the count is
 * a fact about a decision, and the queue's own rows are the ones nobody has
 * decided yet. `ReportRow.confirmedCount` is declared because the contract
 * declares it — admin-bff answers `null` for every row (`ReportQueue.QueueAsync`
 * maps it so, because safety-svc's internal row does not carry it), which is why
 * the screen never prints a strike total it was not handed.
 *
 * ## A suspension has no duration
 *
 * `ReasonBody` is one field. admin-bff sets `registry.vehicles.dispatch_state` to
 * `DISPATCH_SUSPENDED` / `iam.users.is_blocked` to true and nothing anywhere
 * schedules a reinstatement — so the wireframe's "Temporary — 24h ▾" names a
 * control the platform cannot honour. See the C107 handoff.
 */

import type { CursorPage } from './types';

export type { CursorPage };

/** `GET /v1/admin/reports/queue` — the pending vehicle-report inbox. */
export const REPORT_QUEUE_PATH = '/v1/admin/reports/queue';

/** `POST /v1/admin/reports/{reportId}/resolve`. */
export const REPORTS_PATH = '/v1/admin/reports';

/** `POST /v1/admin/vehicles/{vehicleId}/suspend`. */
export const VEHICLES_PATH = '/v1/admin/vehicles';

/** `POST /v1/admin/drivers/{driverId}/suspend`. */
export const DRIVERS_PATH = '/v1/admin/drivers';

/**
 * The contract's maximum page size, and what the queue is asked for.
 *
 * Cursor pagination carries no total, so "how many reports are waiting" is the
 * number of rows this page answered — `100+` past it. The same rule SCR-AP-003's
 * tab badges are written under, for the same reason.
 */
export const QUEUE_PAGE_SIZE = 100;

/**
 * Whether a value is an id this portal will interpolate into an API path.
 *
 * admin-bff routes every moderation operation on `{…:guid}`, so anything else is
 * a 404 there — but it would be a 404 reached by putting a string somebody typed
 * into a path this process builds. The suspend card takes an id **from an
 * operator**, which is the case this guard exists for: it is checked before the
 * request rather than after the answer.
 */
const ADMIN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isAdminId(value: string | undefined | null): value is string {
  return typeof value === 'string' && ADMIN_ID.test(value);
}

export function reportResolvePath(reportId: string): string {
  return `${REPORTS_PATH}/${reportId}/resolve`;
}

/** The two subjects US-14.3 suspends, and the wireframe's "Driver / vehicle ID". */
export const SUSPEND_SUBJECTS = ['driver', 'vehicle'] as const;

export type SuspendSubject = (typeof SUSPEND_SUBJECTS)[number];

export function isSuspendSubject(value: string | undefined): value is SuspendSubject {
  return value !== undefined && (SUSPEND_SUBJECTS as readonly string[]).includes(value);
}

export function suspendPath(subject: SuspendSubject, subjectId: string): string {
  return `${subject === 'vehicle' ? VEHICLES_PATH : DRIVERS_PATH}/${subjectId}/suspend`;
}

/** `safety.vehicle_reports.status` — the three the contract admits. */
export type ReportStatus = 'PENDING' | 'CONFIRMED' | 'DISMISSED';

/** The two verdicts `POST …/resolve` takes. */
export const REPORT_DECISIONS = ['CONFIRMED', 'DISMISSED'] as const;

export type ReportDecision = (typeof REPORT_DECISIONS)[number];

export function isReportDecision(value: string | undefined): value is ReportDecision {
  return value !== undefined && (REPORT_DECISIONS as readonly string[]).includes(value);
}

/** `ReportRow` — one pending report against one vehicle. */
export interface ReportRow {
  readonly reportId: string;
  readonly vehicleId: string;
  /** Who reported it. admin-bff answers `null`: the reporter is not the moderator's business. */
  readonly reporterId?: string | null;
  readonly reason: string;
  readonly status: ReportStatus;
  /** Declared by the contract, answered `null` on a queue row — see this file's note. */
  readonly confirmedCount?: number | null;
  readonly createdAt: string;
}

/** What `POST /v1/admin/reports/{id}/resolve` left behind. */
export interface ResolvedReport {
  readonly reportId: string;
  readonly status: ReportStatus;
  /** How many confirmed reports the vehicle now has. Three is the delisting (US-12.6). */
  readonly confirmedCount?: number;
  readonly vehicleDelisted?: boolean;
}

/** `ModerationResult` — the answer to either suspension. */
export interface ModerationResult {
  readonly subjectId: string;
  readonly status: 'SUSPENDED' | 'ACTIVE';
  readonly reason?: string;
}

/** How many confirmations delist a vehicle (US-12.6). Stated on the screen, enforced by safety-svc. */
export const DELISTING_THRESHOLD = 3;

/**
 * What SCR-AP-004 is currently showing, resolved from the URL.
 *
 * The suspend card is prefilled from the URL rather than from client state, so
 * "suspend the vehicle on this row" is a `<Link>` — no JavaScript, survives a
 * reload, and the operator can see in the address bar exactly which subject the
 * form is aimed at before they press a button that ends somebody's shift.
 */
export interface ModerationSelection {
  /** The subject the suspend card is aimed at, when a row sent the operator there. */
  readonly subject?: SuspendSubject;
  readonly subjectId?: string;
  /** The verdict just recorded, so the queue can confirm what left it. */
  readonly decided?: ReportDecision;
  /** The vehicle's confirmed-report total after that verdict. */
  readonly strikes?: number;
  readonly delisted?: boolean;
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

export function moderationSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): ModerationSelection {
  const subject = first(params.subject);
  const subjectId = first(params.subjectId);
  const decided = first(params.decided);
  const strikes = Number.parseInt(first(params.strikes) ?? '', 10);

  return {
    ...(isSuspendSubject(subject) ? { subject } : {}),
    ...(isAdminId(subjectId) ? { subjectId } : {}),
    ...(isReportDecision(decided) ? { decided } : {}),
    ...(Number.isInteger(strikes) && strikes >= 0 ? { strikes } : {}),
    ...(first(params.delisted) === 'true' ? { delisted: true } : {}),
  };
}

/** The link a queue row's "Suspend vehicle" control is: the card, aimed and anchored. */
export function suspendHref(subject: SuspendSubject, subjectId: string): string {
  return `/reports?subject=${subject}&subjectId=${encodeURIComponent(subjectId)}#suspend`;
}
