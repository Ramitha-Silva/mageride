import type { StatusTone } from '@mageride/ui';

import type {
  DocumentRef,
  DriverQueueRow,
  ExtractedField,
  OrgQueueRow,
  PayoutProfileStatus,
  VehicleQueueRow,
  VerificationStatus,
  VerificationStep,
} from '@/api/verification';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import { formatConfidence, formatCount, formatDateTime } from '@/i18n/format';

/**
 * SCR-AP-003/003a/003c's view model: the arithmetic and the vocabulary, apart
 * from the markup that draws them.
 *
 * Everything here is pure, which is what lets the rules the DoD is actually about
 * be asserted directly — that Approve is locked while a field is unconfirmed,
 * that a plate mismatch reads differently from a doubtful scan, that an
 * auto-verified row offers no decision.
 *
 * ## Why there are vocabulary tables here at all
 *
 * `field.key`, `document.kind` and `step.step` are **stored identifiers**, not
 * copy: `nic_no`, `revenue_license`, `photos`. They arrive in one spelling for
 * every operator, and the screen has to put a Sinhala word in front of a Sinhala
 * reader — so the mapping from identifier to resource key lives on this side,
 * where all three languages are.
 *
 * **An unknown identifier renders as itself.** admin-bff can add a field key
 * ahead of this portal (AL-29 extracts what the model finds), and the choice is
 * between a raw `licence_class` in the table and an empty cell. The raw key is
 * ugly, obviously wrong and reported; an empty cell is a field an officer decides
 * on without knowing what it is. `../../i18n/index.ts` makes the same call for a
 * nav key this build has not heard of.
 */

export interface PillView {
  readonly tone: StatusTone;
  readonly label: string;
}

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

// ---------------------------------------------------------------------------
// Vocabularies
// ---------------------------------------------------------------------------

/** `registry.document_fields.field_key` — the keys migration 0305 names. */
const FIELD_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  licence_no: 'admin.verification.field.licenceNo',
  licence_expiry: 'admin.verification.field.licenceExpiry',
  nic_no: 'admin.verification.field.nicNo',
  allowed_vehicle_types: 'admin.verification.field.allowedVehicleTypes',
  insurance_expiry: 'admin.verification.field.insuranceExpiry',
  revenue_no: 'admin.verification.field.revenueNo',
  revenue_expiry: 'admin.verification.field.revenueExpiry',
  reg_no_match: 'admin.verification.field.regNoMatch',
};

/**
 * `registry.documents.kind` plus the AL-49 payout slots, which are `docs.uploads`
 * rows and have no `registry.documents` row at all.
 */
const DOCUMENT_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  driving_license: 'admin.verification.doc.drivingLicense',
  registration: 'admin.verification.doc.registration',
  permit: 'admin.verification.doc.permit',
  insurance: 'admin.verification.doc.insurance',
  revenue_license: 'admin.verification.doc.revenueLicense',
  vehicle_photo: 'admin.verification.doc.vehiclePhoto',
  bank_statement: 'admin.verification.doc.bankStatement',
  passbook_first_page: 'admin.verification.doc.passbookFirstPage',
  proof_of_account: 'admin.verification.doc.proofOfAccount',
  lankaqr_code: 'admin.verification.doc.lankaQr',
};

/**
 * The decision rail's rows.
 *
 * **Two vocabularies, because there are two onboarding surfaces** (C063). A Mode C
 * vehicle's steps are AL-30's four saved rows — `details`, `insurance`, `revenue`,
 * `photos`; a fleet vehicle has none, so its rail is AL-50's named slots under the
 * names SCR-FP-004 showed the operator who uploaded them. A driver is the single
 * synthetic `profile`, an organisation the single `kyc`.
 */
const STEP_LABELS: Readonly<Record<string, AdminMessageKey>> = {
  profile: 'admin.verification.step.profile',
  details: 'admin.verification.step.details',
  insurance: 'admin.verification.step.insurance',
  revenue: 'admin.verification.step.revenue',
  revenue_license: 'admin.verification.step.revenue',
  photos: 'admin.verification.step.photos',
  registration: 'admin.verification.step.registration',
  permit: 'admin.verification.step.permit',
  kyc: 'admin.verification.step.kyc',
};

function label(
  table: Readonly<Record<string, AdminMessageKey>>,
  key: string,
  t: AdminTranslator,
): string {
  const resource = table[key];
  return resource ? t(resource) : key;
}

/** A `registry.document_fields.field_key`, in the operator's language. */
export function fieldLabel(key: string, t: AdminTranslator): string {
  return label(FIELD_LABELS, key, t);
}

/** A document kind, in the operator's language. */
export function documentLabel(kind: string, t: AdminTranslator): string {
  return label(DOCUMENT_LABELS, kind, t);
}

/** A decision-rail step, in the operator's language. */
export function stepLabel(step: string, t: AdminTranslator): string {
  return label(STEP_LABELS, step, t);
}

// ---------------------------------------------------------------------------
// The queues (SCR-AP-003)
// ---------------------------------------------------------------------------

/**
 * One cell of the two the middle of a queue row carries. The three tabs answer
 * different questions there — when a submission arrived, or how big the
 * organisation is — and a table that changed shape between tabs would make the
 * status column move under the officer's eye as they switch.
 */
export interface QueueCellView {
  readonly text?: string;
  readonly pills?: readonly PillView[];
}

export interface QueueRowView {
  readonly key: string;
  /** The detail screen this row opens. */
  readonly href: string;
  /** A person's name, a plate, an organisation. */
  readonly primary: string;
  /** The subject id, printed in full — a truncated identifier is an ambiguous one. */
  readonly secondary: string;
  readonly cells: readonly [QueueCellView, QueueCellView];
  readonly status: PillView;
}

/**
 * The status pill.
 *
 * `PENDING` carries the number of flagged fields because that is the size of the
 * job the row represents — the wireframe's "Pending · 2". `APPROVED` and
 * `REJECTED` are the two cases C063's queue predicate admits alongside it: a
 * renewal flagged on somebody already approved, and a resubmission after a
 * refusal. Neither is a queue that has stopped filtering; both are subjects with
 * a field still `pending`.
 */
export function statusPill(
  status: VerificationStatus,
  flagged: number,
  t: AdminTranslator,
): PillView {
  if (status === 'APPROVED') return { tone: 'success', label: t('status.approved') };
  if (status === 'REJECTED') return { tone: 'error', label: t('status.rejected') };

  return {
    tone: 'warning',
    label:
      flagged > 0
        ? t('admin.verification.status.pendingCount', { count: flagged })
        : t('status.pending'),
  };
}

function flaggedCell(flagged: readonly string[], t: AdminTranslator): QueueCellView {
  if (flagged.length === 0) return { text: '—' };
  return { text: flagged.map((key) => fieldLabel(key, t)).join(' · ') };
}

export function driverQueueRows(
  rows: readonly DriverQueueRow[],
  href: (id: string) => string,
  { t, locale }: RenderContext,
): QueueRowView[] {
  return rows.map((row) => ({
    key: row.driverId,
    href: href(row.driverId),
    primary: row.name,
    secondary: row.driverId,
    cells: [
      { text: formatDateTime(locale, row.submittedAt) ?? '—' },
      flaggedCell(row.flaggedFields, t),
    ],
    status: statusPill(row.status, row.flaggedFields.length, t),
  }));
}

export function vehicleQueueRows(
  rows: readonly VehicleQueueRow[],
  href: (id: string) => string,
  { t, locale }: RenderContext,
): QueueRowView[] {
  return rows.map((row) => ({
    key: row.vehicleId,
    href: href(row.vehicleId),
    primary: row.regNo,
    secondary: row.vehicleId,
    cells: [
      { text: formatDateTime(locale, row.submittedAt) ?? '—' },
      flaggedCell(row.flaggedFields, t),
    ],
    status: statusPill(row.status, row.flaggedFields.length, t),
  }));
}

/**
 * The fleet-org tab.
 *
 * Its middle columns are the organisation's size and the **two** verdicts an
 * officer is here to give: whether there is US-13.A7 evidence to read
 * (`kycStatus`) and where the AL-49 payout profile stands. The contract keeps
 * them apart deliberately, and one pill could not carry both.
 */
export function orgQueueRows(
  rows: readonly OrgQueueRow[],
  href: (id: string) => string,
  { t }: RenderContext,
): QueueRowView[] {
  return rows.map((row) => ({
    key: row.orgId,
    href: href(row.orgId),
    primary: row.name,
    secondary: row.orgId,
    cells: [
      { text: t('admin.verification.org.vehicleCount', { count: row.vehicleCount }) },
      {
        pills: [
          row.kycStatus === 'complete'
            ? { tone: 'info', label: t('admin.verification.org.kycComplete') }
            : { tone: 'warning', label: t('admin.verification.org.kycIncomplete') },
          ...(row.payoutProfileStatus ? [payoutPill(row.payoutProfileStatus, t)] : []),
        ],
      },
    ],
    status: statusPill(row.status, 0, t),
  }));
}

/** The AL-49 payout profile's own state, which approving the organisation flips. */
export function payoutPill(status: PayoutProfileStatus | 'superseded', t: AdminTranslator): PillView {
  if (status === 'verified') return { tone: 'success', label: t('admin.verification.payout.verified') };
  if (status === 'rejected') return { tone: 'error', label: t('admin.verification.payout.rejected') };
  if (status === 'superseded')
    return { tone: 'neutral', label: t('admin.verification.payout.superseded') };

  return { tone: 'warning', label: t('admin.verification.payout.pending') };
}

/**
 * A tab's badge.
 *
 * Cursor pagination carries no total, so the exact figure is the number of rows
 * the queue answered — and where there are more, the badge says so rather than
 * reporting a page size as a backlog. A queue that could not be read is `—`, not
 * `0`: "nothing waiting" is a claim, and a 503 is not evidence for it.
 */
export function queueCount(
  page: { readonly items: readonly unknown[]; readonly hasMore: boolean } | null,
  { t, locale }: RenderContext,
): string {
  if (!page) return '—';

  return page.hasMore
    ? t('admin.verification.queue.countMore', { count: page.items.length })
    : formatCount(locale, page.items.length);
}

// ---------------------------------------------------------------------------
// The detail (SCR-AP-003a)
// ---------------------------------------------------------------------------

export interface FieldRowView {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  readonly source: PillView;
  readonly status: PillView;
  /**
   * Whether this row carries the Confirm / Edit & confirm pair.
   *
   * **Only a `pending` field does** — AL-27's fence, again, on the row that
   * enforces it. Confirming a field nobody flagged is a decision with no question
   * in front of it, and C063 refuses it upstream with a `404`; drawing the button
   * would be this screen offering an action the platform declines.
   */
  readonly decidable: boolean;
  /**
   * Accessible names for the row's two controls.
   *
   * Six rows carrying six buttons that all announce "Confirm" is a list a screen
   * reader cannot act on, and the visible label has to stay short. So the full
   * sentence is built here, where the translator and the field's own label both
   * are.
   */
  readonly confirmNamed: string;
  readonly editNamed: string;
}

/**
 * One extracted field as SCR-AP-003a draws it.
 *
 * The status wording is the wireframe's three cases, and each is derivable:
 * `reg_no_match` still pending is the **plate mismatch** (AL-30's cross-check
 * failed), a pending AI value is a **doubtful** scan, and a pending manual value
 * is a driver's own entry (US-2.4a) with nothing to doubt — just nothing verified.
 */
export function fieldRows(
  fields: readonly ExtractedField[],
  { t, locale }: RenderContext,
): FieldRowView[] {
  return fields.map((field) => {
    const name = fieldLabel(field.key, t);

    return {
      key: field.key,
      label: name,
      value: field.value ?? '—',
      source: sourcePill(field, { t, locale }),
      status: fieldStatusPill(field, t),
      decidable: field.verifyStatus === 'pending',
      confirmNamed: t('admin.verification.field.confirmNamed', { field: name }),
      editNamed: t('admin.verification.field.editNamed', { field: name }),
    };
  });
}

function sourcePill(field: ExtractedField, { t, locale }: RenderContext): PillView {
  if (field.source === 'manual') {
    return { tone: 'warning', label: t('admin.verification.source.manual') };
  }

  const confidence = formatConfidence(locale, field.confidence);
  return {
    tone: 'neutral',
    label: confidence
      ? t('admin.verification.source.aiScored', { confidence })
      : t('admin.verification.source.ai'),
  };
}

function fieldStatusPill(field: ExtractedField, t: AdminTranslator): PillView {
  if (field.verifyStatus === 'auto_verified') {
    return { tone: 'success', label: t('admin.verification.fieldStatus.autoVerified') };
  }
  if (field.verifyStatus === 'confirmed') {
    return { tone: 'success', label: t('admin.verification.fieldStatus.confirmed') };
  }
  if (field.key === 'reg_no_match') {
    return { tone: 'error', label: t('admin.verification.fieldStatus.pendingMismatch') };
  }
  if (field.source === 'ai') {
    return { tone: 'warning', label: t('admin.verification.fieldStatus.pendingDoubtful') };
  }
  return { tone: 'warning', label: t('status.pending') };
}

export interface StepRowView {
  readonly key: string;
  readonly label: string;
  readonly status: PillView;
}

export function stepRows(steps: readonly VerificationStep[], t: AdminTranslator): StepRowView[] {
  return steps.map((step) => ({
    key: step.step,
    label: stepLabel(step.step, t),
    status:
      step.status === 'VERIFIED'
        ? { tone: 'success', label: t('status.verified') }
        : {
            // `PENDING_INPUT` is "nothing has been uploaded yet" and
            // `PENDING_REVIEW` is "somebody has to look" — the wireframe draws
            // the first in the error tone, because it is the officer's cue that
            // the applicant, not the queue, is what this step is waiting on.
            tone: step.status === 'PENDING_INPUT' ? 'error' : 'warning',
            label:
              step.status === 'PENDING_INPUT'
                ? t('admin.verification.step.awaitingUpload')
                : t('status.pending'),
          },
  }));
}

export interface DocumentTileView {
  readonly docId: string;
  readonly label: string;
  /** The lightbox this tile opens (SCR-AP-003b). */
  readonly href: string;
  /** The portal's own relay of the audited thumbnail. */
  readonly src: string;
  /** `1 / 6` — the wireframe's paging counter, drawn on the tile. */
  readonly position: string;
  /** AL-43 provenance, when the upload recorded one. */
  readonly capturedVia?: string;
}

export function documentTiles(
  documents: readonly DocumentRef[],
  links: { readonly viewer: (docId: string) => string; readonly media: (docId: string) => string },
  { t }: RenderContext,
): DocumentTileView[] {
  return documents.map((document, index) => ({
    docId: document.docId,
    label: documentLabel(document.kind, t),
    href: links.viewer(document.docId),
    src: links.media(document.docId),
    position: t('admin.verification.doc.position', {
      index: index + 1,
      total: documents.length,
    }),
    ...(document.capturedVia
      ? {
          capturedVia:
            document.capturedVia === 'drag_crop'
              ? t('admin.verification.doc.capturedDragCrop')
              : t('admin.verification.doc.capturedUpload'),
        }
      : {}),
  }));
}

/**
 * Which document the lightbox is on, and the two it pages to.
 *
 * `null` for an id that is not in this subject's set — the neighbours would
 * otherwise be computed from `-1` and the viewer would open on somebody else's
 * first document.
 */
export interface ViewerPosition {
  readonly index: number;
  readonly current: DocumentTileView;
  readonly previous?: DocumentTileView;
  readonly next?: DocumentTileView;
}

export function viewerPosition(
  tiles: readonly DocumentTileView[],
  docId: string,
): ViewerPosition | null {
  const index = tiles.findIndex((tile) => tile.docId === docId);
  if (index < 0) return null;

  const current = tiles[index];
  if (!current) return null;

  const previous = tiles[index - 1];
  const next = tiles[index + 1];

  return {
    index,
    current,
    ...(previous ? { previous } : {}),
    ...(next ? { next } : {}),
  };
}

/**
 * The heading pill: how much is still open on this subject.
 *
 * Counted from the fields rather than taken from `approvable`, because the
 * officer needs the size of the job and not only whether it is finished — and
 * when it *is* finished the pill has to say so, or a screen with every row green
 * still reads "Pending review".
 */
export function pendingSummary(
  fields: readonly ExtractedField[],
  approvable: boolean,
  t: AdminTranslator,
): PillView {
  const pending = fields.filter((field) => field.verifyStatus === 'pending').length;

  if (pending === 0) {
    return approvable
      ? { tone: 'success', label: t('admin.verification.detail.readyToApprove') }
      : { tone: 'warning', label: t('admin.verification.detail.pendingReview') };
  }

  return { tone: 'warning', label: t('admin.verification.detail.pendingFields', { count: pending }) };
}
