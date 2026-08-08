/**
 * SCR-AP-003/003a/003b/003c's wire shapes, and the one query the three queues
 * share.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` — the `AL-39` block:
 * `listDrivingLicenseQueue`, `listVehicleRegistrationQueue`, `listFleetOrgQueue`,
 * `getVerificationSubject`, `getOrgVerification`, `decideVerificationField`,
 * `approveVerificationSubject`, `rejectVerificationSubject` and `viewDocument`.
 * They live here rather than in `./types.ts` for the reason that file states: the
 * shell owns the shapes the *shell* needs.
 *
 * **One search box and one status filter, over three queues.** D2 §SCR-AP-003
 * requires "search + status filter" on the screen, not per tab, and the wireframe
 * draws one search in the topbar above all three. So {@link queueSearch} builds
 * one query and the page sends it to all three routes — which is also what makes
 * the three tab counts answer the same question at the same moment.
 *
 * **`status` is the subject's own registration status, never the queue's.** Queue
 * membership is "a `registry.document_fields` row is still `pending`" — that is
 * AL-27's fence stated as the query it is, and C063's own file says so. Every row
 * is therefore awaiting review by construction; what the filter distinguishes is a
 * renewal flagged on an already-approved applicant (`APPROVED`) from a
 * resubmission after a refusal (`REJECTED`).
 */

/** The three queues D2 §SCR-AP-003 puts on the screen, in the wireframe's order. */
export const VERIFICATION_QUEUES = ['driving-license', 'vehicle-registration', 'fleet-org'] as const;

export type VerificationQueue = (typeof VERIFICATION_QUEUES)[number];

export const QUEUE_PATHS: Readonly<Record<VerificationQueue, string>> = {
  'driving-license': '/v1/admin/verification/queues/driving-license',
  'vehicle-registration': '/v1/admin/verification/queues/vehicle-registration',
  'fleet-org': '/v1/admin/verification/queues/fleet-org',
};

/** The subject-agnostic detail, confirm, approve and reject family (AL-39). */
export const VERIFICATION_PATH = '/v1/admin/verification';

/** `GET /v1/admin/verification/org/{orgId}` — SCR-AP-003c's own read. */
export const ORG_VERIFICATION_PATH = '/v1/admin/verification/org';

/** `GET /v1/admin/documents/{docId}` — the audited viewer (SCR-AP-003b). */
export const DOCUMENTS_PATH = '/v1/admin/documents';

/**
 * The contract's maximum page size, and what every queue is asked for.
 *
 * The wireframe draws a count on each tab and their sum in the topbar, and
 * cursor pagination carries no total — so the exact count is the number of rows
 * a queue answers, and beyond one page the screen says "100+" rather than
 * inventing a figure. An officer's backlog past a hundred is worked with the
 * search box, which is why D2 gives the screen one and no pager.
 */
export const QUEUE_PAGE_SIZE = 100;

/** `_shared.yaml#/components/schemas/CursorPage`, with the items typed. */
export interface CursorPage<T> {
  readonly items: readonly T[];
  readonly cursor: string | null;
  readonly hasMore: boolean;
}

/** The subject's own registration status — what the SCR-AP-003 filter filters on. */
export const VERIFICATION_STATUSES = ['PENDING', 'APPROVED', 'REJECTED'] as const;

export type VerificationStatus = (typeof VERIFICATION_STATUSES)[number];

/** `DriverQueueRow` — the driving-licence tab. */
export interface DriverQueueRow {
  readonly driverId: string;
  readonly name: string;
  readonly submittedAt: string;
  readonly flaggedFields: readonly string[];
  readonly status: VerificationStatus;
}

/** `VehicleQueueRow` — the vehicle-registration tab, Mode C and fleet alike (AL-50). */
export interface VehicleQueueRow {
  readonly vehicleId: string;
  readonly regNo: string;
  readonly ownerDriverId?: string;
  readonly submittedAt?: string;
  readonly flaggedFields: readonly string[];
  readonly status: VerificationStatus;
}

/**
 * `OrgQueueRow` — the fleet-org tab.
 *
 * `kycStatus` and `payoutProfileStatus` are two decisions and the contract keeps
 * them apart deliberately: `complete` says there is US-13.A7 evidence to read at
 * all, and the AL-49 payout profile has its own state beside it.
 */
export interface OrgQueueRow {
  readonly orgId: string;
  readonly name: string;
  readonly kycStatus: 'complete' | 'incomplete';
  readonly vehicleCount: number;
  readonly status: VerificationStatus;
  readonly payoutProfileStatus?: PayoutProfileStatus;
}

export type PayoutProfileStatus = 'pending_verification' | 'verified' | 'rejected';

export type QueueRow = DriverQueueRow | VehicleQueueRow | OrgQueueRow;

/**
 * `DocumentRef` — one stored document and the two links it is opened by.
 *
 * **The URLs are not used as image sources.** Both point at
 * `GET /v1/admin/documents/{docId}`, which is admin-bff's audited viewer: it
 * records `DOC_VIEW` and *then* mints the short-lived signed object-storage URL it
 * redirects to. The browser cannot call it — it holds no bearer and never talks to
 * the gateway — so the portal relays it through its own `/verification/media/…`
 * route and the fetch is built from `docId` rather than from a string upstream
 * supplied. The row is written either way; what this avoids is putting an
 * upstream-controlled string into an `src`.
 */
export interface DocumentRef {
  readonly docId: string;
  readonly kind: string;
  readonly thumbUrl?: string;
  readonly fullUrl?: string;
  /** AL-43 provenance: `drag_crop` for the in-app scanner, `upload` otherwise. */
  readonly capturedVia?: 'drag_crop' | 'upload';
}

/** `_shared.yaml#/components/schemas/ExtractedField` — one AL-29/AL-30 field. */
export interface ExtractedField {
  /** A `registry.document_fields.field_key`, snake case: `nic_no`, `reg_no_match`, … */
  readonly key: string;
  readonly value: string | null;
  readonly source: 'ai' | 'manual';
  /** Gemini Flash 3.0's score. Absent on a manual value — a `ck_document_fields_manual_confidence` rule. */
  readonly confidence?: number | null;
  readonly verifyStatus: 'auto_verified' | 'pending' | 'confirmed';
}

export type StepStatus = 'VERIFIED' | 'PENDING_REVIEW' | 'PENDING_INPUT';

/** One row of SCR-AP-003a's decision rail. */
export interface VerificationStep {
  readonly step: string;
  readonly status: StepStatus;
}

/** `VerificationDetail` — SCR-AP-003a. */
export interface VerificationDetail {
  readonly subject: {
    readonly id: string;
    readonly type: 'driver' | 'vehicle' | 'org';
    readonly displayName?: string;
  };
  readonly fields: readonly ExtractedField[];
  readonly documents: readonly DocumentRef[];
  readonly steps: readonly VerificationStep[];
  /** **False while any field is still `pending`** (US-2.10a). The Approve gate. */
  readonly approvable?: boolean;
}

/**
 * The organisation KYC an officer reads before approving (US-13.A7).
 *
 * `admin-bff.yaml` types `kyc` as a free-form object; `OrgKycResponse` in
 * `backend/src/AdminBff/Endpoints/AdminContracts.cs` is what it actually answers,
 * and this is that record. See the C106 handoff — the contract should carry the
 * schema the code already has.
 */
export interface OrgKyc {
  readonly orgId: string;
  readonly name: string;
  readonly registrationNo?: string | null;
  readonly contactPhone?: string | null;
  readonly contactEmail?: string | null;
  readonly address?: string | null;
  readonly status: VerificationStatus;
  readonly rejectionReason?: string | null;
  readonly payoutProfile?: OrgPayout | null;
}

/** The AL-49 bank details the Mode B pay sheet renders once they are verified. */
export interface OrgPayout {
  readonly bank: string;
  readonly branch: string;
  readonly accountNo: string;
  readonly accountHolderName: string;
  readonly status: PayoutProfileStatus | 'superseded';
  readonly rejectionReason?: string | null;
  readonly verifiedAt?: string | null;
}

/** `GET /v1/admin/verification/org/{orgId}` — SCR-AP-003c. */
export interface OrgVerification {
  readonly kyc: OrgKyc;
  readonly payoutProfileStatus?: PayoutProfileStatus | null;
  readonly documents: readonly DocumentRef[];
}

/** What one `PUT …/fields/{key}` left behind. */
export interface FieldDecision {
  readonly field: ExtractedField;
  readonly stepStatus: StepStatus;
  readonly approvable?: boolean;
}

/** `VerificationDecision` — the answer to approve and to reject. */
export interface VerificationDecision {
  readonly subjectId: string;
  readonly status: 'APPROVED' | 'REJECTED';
  readonly reason?: string;
  /** **Δ AL-57 — always false.** D-11 is retired and nothing sets it. */
  readonly merchantBound?: boolean;
}

/**
 * Whether a path segment is an id this portal will put into an API path.
 *
 * admin-bff routes the whole family on `{subjectId:guid}`, so anything else is a
 * 404 there — but it would be a 404 reached by interpolating whatever was in the
 * URL into a path this process builds, and a data layer that only ever sends
 * shapes it has checked is one fewer thing to reason about. It also settles
 * `/verification/expiring`, which is a *different screen* the router would
 * otherwise hand to the detail page.
 */
const SUBJECT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isSubjectId(value: string | undefined): value is string {
  return value !== undefined && SUBJECT_ID.test(value);
}

export function subjectPath(subjectId: string): string {
  return `${VERIFICATION_PATH}/${subjectId}`;
}

export function subjectFieldPath(subjectId: string, fieldKey: string): string {
  return `${VERIFICATION_PATH}/${subjectId}/fields/${encodeURIComponent(fieldKey)}`;
}

export function subjectApprovePath(subjectId: string): string {
  return `${VERIFICATION_PATH}/${subjectId}/approve`;
}

export function subjectRejectPath(subjectId: string): string {
  return `${VERIFICATION_PATH}/${subjectId}/reject`;
}

export function orgPath(orgId: string): string {
  return `${ORG_VERIFICATION_PATH}/${orgId}`;
}

export function documentPath(docId: string): string {
  return `${DOCUMENTS_PATH}/${docId}`;
}

/** What the screen is currently asking of all three queues, resolved from the URL. */
export interface QueueSelection {
  readonly queue: VerificationQueue;
  /** Case-insensitive substring over the name, the plate, or the subject id. */
  readonly search?: string;
  readonly status?: VerificationStatus;
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

function isQueue(value: string | undefined): value is VerificationQueue {
  return value !== undefined && (VERIFICATION_QUEUES as readonly string[]).includes(value);
}

function isStatus(value: string | undefined): value is VerificationStatus {
  return value !== undefined && (VERIFICATION_STATUSES as readonly string[]).includes(value);
}

/**
 * The selection, from the page's own search parameters.
 *
 * An unrecognised tab or status falls back to "the first tab, unfiltered" rather
 * than being sent on: `VerificationStatus` is a closed enum admin-bff answers
 * `400` for, and a pasted or edited URL would otherwise greet the officer with a
 * validation error about a control they did not touch.
 */
export function queueSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): QueueSelection {
  const queue = first(params.queue);
  const status = first(params.status);
  const search = first(params.search)?.trim();

  return {
    queue: isQueue(queue) ? queue : VERIFICATION_QUEUES[0],
    ...(search ? { search: search.slice(0, 100) } : {}),
    ...(isStatus(status) ? { status } : {}),
  };
}

/**
 * The selection as admin-bff's query — the same one for all three routes.
 *
 * The tab is deliberately absent: it chooses which route is called, and a queue
 * that also received it would be a parameter no contract declares.
 */
export function queueSearch(selection: QueueSelection): Record<string, string | number> {
  return {
    ...(selection.search ? { search: selection.search } : {}),
    ...(selection.status ? { status: selection.status } : {}),
    limit: QUEUE_PAGE_SIZE,
  };
}

/** The selection as a portal query string, for the tab links and the row links. */
export function queueQuery(selection: QueueSelection): Record<string, string> {
  return {
    queue: selection.queue,
    ...(selection.search ? { search: selection.search } : {}),
    ...(selection.status ? { status: selection.status } : {}),
  };
}

/** `path` with this selection's query on it — the filter is the URL and nothing else. */
export function queueHref(
  path: string,
  selection: QueueSelection,
  overrides: Partial<QueueSelection> = {},
): string {
  const query = new URLSearchParams(queueQuery({ ...selection, ...overrides }));
  return `${path}?${query.toString()}`;
}
