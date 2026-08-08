import {
  feedLabel,
  isActivatable,
  isPending,
  isRollback,
  type FeedCounts,
  type FeedStatus,
  type FeedUploadStatus,
  type FeedVersion,
} from '@/api/transit';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import { formatBusinessDate, formatCount, formatDateTime } from '@/i18n/format';

/**
 * SCR-AP-016's view model — the six statuses, the four-step stepper, the counts
 * grid and the history row.
 *
 * Everything that turns wire values into strings lives here rather than in the
 * components, for the reason C105 established and every screen group since has
 * followed: the interactive parts of this screen are client components, and a
 * translator does not cross the RSC boundary. The page resolves the copy; the
 * components take it as props.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

/* ---------------------------------------------------------------------------
 * Status
 * ------------------------------------------------------------------------ */

const STATUS_LABEL = {
  uploaded: 'admin.transit.status.uploaded',
  validating: 'admin.transit.status.validating',
  validated: 'admin.transit.status.validated',
  failed: 'admin.transit.status.failed',
  active: 'admin.transit.status.active',
  archived: 'admin.transit.status.archived',
} as const satisfies Record<FeedStatus, AdminMessageKey>;

/**
 * D2 draws four chips and the wireframe colours them; these are the tones.
 *
 * `validated` is `info` and not `success` on purpose — a validated feed has passed
 * its checks and is serving nobody. Green is reserved for the one row that is
 * actually answering passenger route queries, because "which dataset is live" is
 * the single question this screen exists to make unmissable.
 */
const STATUS_TONE = {
  uploaded: 'neutral',
  validating: 'info',
  validated: 'info',
  failed: 'error',
  active: 'success',
  archived: 'neutral',
} as const satisfies Record<FeedStatus, 'neutral' | 'info' | 'success' | 'warning' | 'error'>;

export type StatusTone = (typeof STATUS_TONE)[FeedStatus];

export function statusLabel(status: FeedStatus, { t }: RenderContext): string {
  return t(STATUS_LABEL[status]);
}

export function statusTone(status: FeedStatus): StatusTone {
  return STATUS_TONE[status];
}

/* ---------------------------------------------------------------------------
 * The stepper — `Uploaded → Validating → Validated / Failed`
 * ------------------------------------------------------------------------ */

export type StepState = 'done' | 'current' | 'todo';

export interface StepView {
  readonly id: 'uploaded' | 'validating' | 'outcome';
  readonly label: string;
  readonly state: StepState;
  /** True on the terminal step when the verdict was `failed`, so it renders red. */
  readonly failed: boolean;
}

/**
 * The three steps D2 draws, with the fourth being the *outcome* of the third
 * rather than a step of its own.
 *
 * "Validated / Failed" is one position in the sequence with two possible values,
 * and drawing it as two boxes would put a Failed chip on screen beside a Validated
 * one for every feed that ever passed. `active` and `archived` are past that
 * sequence entirely — a feed that has been live has, by definition, validated —
 * so both render the whole rail complete.
 */
export function stepperSteps(status: FeedStatus, { t }: RenderContext): StepView[] {
  const uploaded: StepState = 'done';

  const validating: StepState =
    status === 'uploaded' ? 'todo' : status === 'validating' ? 'current' : 'done';

  const outcome: StepState = isPending(status) ? 'todo' : 'done';

  return [
    { id: 'uploaded', label: t('admin.transit.step.uploaded'), state: uploaded, failed: false },
    {
      id: 'validating',
      label: t('admin.transit.step.validating'),
      state: validating,
      failed: false,
    },
    {
      id: 'outcome',
      label: status === 'failed' ? t('admin.transit.step.failed') : t('admin.transit.step.validated'),
      state: outcome,
      failed: status === 'failed',
    },
  ];
}

/* ---------------------------------------------------------------------------
 * The counts grid
 * ------------------------------------------------------------------------ */

/**
 * D2's six columns first — agencies · routes · trips · stops · stop times ·
 * shapes — then the three other files the validator counts.
 *
 * The keys are GTFS **file** names, which is why they are `stop_times` and not
 * `stopTimes`: transit-svc serialises the dictionary literally, saying "SCR-AP-016's
 * counts grid is labelled from the same keys".
 */
const COUNT_ORDER: readonly string[] = [
  'agency',
  'routes',
  'trips',
  'stops',
  'stop_times',
  'shapes',
  'calendar',
  'calendar_dates',
  'frequencies',
];

const COUNT_LABEL: Readonly<Record<string, AdminMessageKey>> = {
  agency: 'admin.transit.file.agency',
  routes: 'admin.transit.file.routes',
  trips: 'admin.transit.file.trips',
  stops: 'admin.transit.file.stops',
  stop_times: 'admin.transit.file.stopTimes',
  shapes: 'admin.transit.file.shapes',
  calendar: 'admin.transit.file.calendar',
  calendar_dates: 'admin.transit.file.calendarDates',
  frequencies: 'admin.transit.file.frequencies',
};

export interface CountView {
  readonly key: string;
  readonly label: string;
  readonly value: string;
}

/**
 * Every file the feed was counted on, in D2's order, then anything else the
 * validator reported.
 *
 * **Nothing is dropped and nothing is invented.** A file the feed omits produces
 * no key, so it does not appear — `shapes` is optional and a feed without it must
 * not show "0 shapes", which reads as an empty file rather than an absent one. A
 * key with no resource string falls back to its own GTFS file name, which is the
 * feed's vocabulary rather than MageRide copy and is therefore not a trilingual
 * gap: it is the same string in all three languages because it is a filename.
 */
export function countRows(
  counts: FeedCounts | undefined,
  { t, locale }: RenderContext,
): CountView[] {
  const entries = Object.entries(counts ?? {});
  if (entries.length === 0) return [];

  const rank = (key: string) => {
    const index = COUNT_ORDER.indexOf(key);
    return index === -1 ? COUNT_ORDER.length : index;
  };

  return entries
    .sort(([left], [right]) => rank(left) - rank(right) || left.localeCompare(right))
    .map(([key, value]) => ({
      key,
      label: COUNT_LABEL[key] ? t(COUNT_LABEL[key]) : `${key}.txt`,
      value: formatCount(locale, value),
    }));
}

/** `counts.routes`, which is the one figure the topbar pill and the history table print. */
export function routeCount(counts: FeedCounts | undefined, { locale }: RenderContext): string | null {
  const routes = counts?.['routes'];
  return typeof routes === 'number' ? formatCount(locale, routes) : null;
}

/* ---------------------------------------------------------------------------
 * The preview card
 * ------------------------------------------------------------------------ */

export interface FeedCardView {
  readonly feedVersionId: string;
  readonly label: string;
  readonly status: FeedStatus;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  readonly steps: readonly StepView[];
  readonly counts: readonly CountView[];
  /** The `feed_info` version, or `null` when the feed omits the optional file. */
  readonly feedInfoVersion: string | null;
  /** `2026-07-22 – 2027-01-31`, or `null` when no calendar could be read. */
  readonly serviceWindow: string | null;
  readonly warnings: readonly string[];
  /** "3 warnings", already counted and formatted — the `<details>` summary. */
  readonly warningsSummary: string;
  /** At most five, by contract. */
  readonly errors: readonly string[];
  readonly pending: boolean;
  readonly activatable: boolean;
  readonly rollback: boolean;
}

export function feedCardView(
  status: FeedUploadStatus,
  context: RenderContext,
): FeedCardView {
  const { t, locale } = context;

  const start = formatBusinessDate(locale, status.serviceStart);
  const end = formatBusinessDate(locale, status.serviceEnd);
  const warnings = status.warnings ?? [];

  return {
    feedVersionId: status.feedVersionId,
    label: feedLabel({
      feedVersionId: status.feedVersionId,
      feedInfoVersion: status.feedInfoVersion ?? null,
    }),
    status: status.status,
    statusLabel: statusLabel(status.status, context),
    statusTone: statusTone(status.status),
    steps: stepperSteps(status.status, context),
    counts: countRows(status.counts, context),
    feedInfoVersion: status.feedInfoVersion?.trim() ? status.feedInfoVersion.trim() : null,
    // Both ends or neither: a window with one end is a range nobody can plan
    // against, and the calendar it came from either parsed or did not.
    serviceWindow: start && end ? t('admin.transit.preview.window', { start, end }) : null,
    warnings,
    warningsSummary: t('admin.transit.preview.warnings', { count: warnings.length }),
    errors: status.errorSummary ?? [],
    pending: isPending(status.status),
    activatable: isActivatable(status.status),
    rollback: isRollback(status.status),
  };
}

/* ---------------------------------------------------------------------------
 * The version-history table
 * ------------------------------------------------------------------------ */

export interface VersionRowView {
  readonly feedVersionId: string;
  readonly label: string;
  readonly fileName: string;
  /** `17 Jun 08:40`, on a Sri Lankan clock. */
  readonly uploadedAt: string | null;
  /** The uploader's **user id** — see `FeedVersion.uploadedBy` for why it is not a name. */
  readonly uploadedBy: string;
  readonly routes: string | null;
  readonly status: FeedStatus;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  /** Whether this row is the one the preview card is currently showing. */
  readonly selected: boolean;
  readonly activatable: boolean;
  readonly rollback: boolean;
}

export function versionRows(
  versions: readonly FeedVersion[],
  selectedId: string | null,
  context: RenderContext,
): VersionRowView[] {
  return versions.map((version) => ({
    feedVersionId: version.feedVersionId,
    label: feedLabel(version),
    fileName: version.fileName,
    uploadedAt: formatDateTime(context.locale, version.uploadedAt),
    uploadedBy: version.uploadedBy,
    routes: routeCount(version.counts, context),
    status: version.status,
    statusLabel: statusLabel(version.status, context),
    statusTone: statusTone(version.status),
    selected: version.feedVersionId === selectedId,
    activatable: isActivatable(version.status),
    rollback: isRollback(version.status),
  }));
}

/* ---------------------------------------------------------------------------
 * Which version the screen is showing
 * ------------------------------------------------------------------------ */

/**
 * The version the preview card renders: the one named by `?feed=`, else the most
 * recent upload.
 *
 * **The newest, whatever its status**, and that is the whole of the state machine
 * D2 lists. It is the last thing that happened, so it is what an operator opening
 * the screen needs to see: a feed still validating, a feed waiting to be
 * activated, a feed that failed and has to be fixed and re-uploaded, or — once the
 * newest upload has been made live — the live feed itself, which is D2's
 * `active-idle`. No version at all is the `empty` day-0 state.
 *
 * `?feed=` is how any other row is opened, and it is the URL rather than component
 * state for C105's reason: a feed under review survives a reload, a bookmark and
 * being pasted into a ticket for a colleague to look at.
 */
export function selectedFeedId(
  versions: readonly FeedVersion[],
  requested: string | undefined,
): string | null {
  return requested ?? versions[0]?.feedVersionId ?? null;
}

/**
 * That version's history row, when this page of history carries it.
 *
 * Best-effort on purpose: `?feed=` may name a version older than the hundred rows
 * read here, and the card is still rendered for it — the status read is by id and
 * needs no row. What the row adds is the file name and the uploader, so their
 * absence costs two captions rather than the screen.
 */
export function versionRow(
  versions: readonly FeedVersion[],
  feedVersionId: string | null,
): FeedVersion | null {
  if (!feedVersionId) return null;
  return versions.find((version) => version.feedVersionId === feedVersionId) ?? null;
}

/** The one live feed, if this page of history carries it. */
export function activeVersion(versions: readonly FeedVersion[]): FeedVersion | null {
  return versions.find((version) => version.status === 'active') ?? null;
}
