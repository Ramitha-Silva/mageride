import { StatusPill, ToastProvider } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  feedLabel,
  gtfsUploadPath,
  GTFS_VERSIONS_PATH,
  isFeedVersionId,
  type FeedUploadStatus,
  type FeedVersionPage,
} from '@/api/transit';
import { configTabs } from '@/components/config/tabs';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenTabs } from '@/components/ScreenTabs';
import { FeedCard } from '@/components/transit/FeedCard';
import {
  activeVersion,
  feedCardView,
  routeCount,
  selectedFeedId,
  versionRow,
  versionRows,
  type RenderContext,
} from '@/components/transit/model';
import { UploadCard } from '@/components/transit/UploadCard';
import { VersionHistory } from '@/components/transit/VersionHistory';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-016 · `gtfs_manager`** — the GTFS Dataset Manager (AL-54/AL-55/AL-56,
 * US-28.1…28.3).
 *
 * The Configuration group's Transit-data entry, and the **only** way a GTFS feed
 * enters this platform. AL-56 retired the acquisition plan outright: the day-0
 * national feed and every refresh are externally provided files, so there is no
 * authoring surface here, no route that can write a GTFS row, and nothing on this
 * screen edits a feed. Upload, validate, preview, activate, roll back — and
 * server-side validation (BR-32.1) is the only quality gate MageRide applies.
 *
 * ## Seven states, one selection
 *
 * D2 lists `empty · uploading · validating · validated-preview · failed-report ·
 * activating · active-idle`. Two of them are client-side moments — the progress
 * bar inside `UploadCard`, the spinner inside `ActivateForm` — and the rest are
 * the *status of the selected version*, which is why the screen has exactly one
 * piece of state and it lives in the URL.
 *
 * **`?feed=` is the whole of it.** Absent, the selection is the most recent
 * upload: the last thing that happened, which is what an operator opening the
 * screen needs to see. That single rule produces the four server-rendered states
 * without a branch — a feed still validating shows the stepper and starts the
 * poll, a validated one shows the preview and Activate, a failed one shows the
 * first five errors and the report, and once the newest upload has been made live
 * the card is the live feed itself, which is `active-idle`. No versions at all is
 * `empty`. Keeping it in the URL rather than in component state is C105's rule:
 * a feed under review survives a reload, a bookmark, and being pasted into a
 * ticket for a colleague to look at.
 *
 * ## Three reads, and the history is one of them for a reason
 *
 * The version list answers three questions at once: the history table, **which
 * feed is live** (the topbar pill and the name every confirm dialog has to state),
 * and the file name of the selected version. There is no "get the active feed"
 * route — `transit.yaml` has six operations and none of them is that — so the
 * live feed is the row whose status is `active`, found in the page that was read
 * anyway rather than by a second call.
 *
 * ## Nothing here decides who may see it
 *
 * There is no role branch on this page and there must not be. The screen is
 * reachable iff `gtfs` is in the menu `GET /v1/admin/session` already filtered
 * through the same evaluator transit-svc's own `RequireMageRideRole(Admin,
 * SuperAdmin)` resolves to — `proxy.ts` has decided that before this file runs, and
 * a Verification Officer, a CSR, a Finance Officer and an Auditor get a 403 on the
 * URL as well as no nav entry. The session is read here for one thing only: the
 * Configuration tab strip, whose tabs are the caller's own menu items.
 */

export const dynamic = 'force-dynamic';

/** The screen's own path — the one `AdminMenu.cs` gives the `gtfs` nav item. */
const SCREEN_PATH = '/config/transit/gtfs';
const REPORT_PATH = `${SCREEN_PATH}/report`;
const ZIP_PATH = `${SCREEN_PATH}/zip`;

/**
 * One page of history, at the contract's ceiling.
 *
 * `_shared.yaml` caps `limit` at 100 and D2 draws the history as a plain table
 * with no pager. A hundred national feed versions is years of refreshes; where
 * there are more, the table says so rather than ending without explanation.
 */
const HISTORY_LIMIT = 100;

export default async function GtfsManagerPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const requested = first((await searchParams)['feed']);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };
  const tabs = configTabs(session?.menu ?? [], 'gtfs');

  let history: FeedVersionPage | null = null;
  let problem: ProblemDetails | null = null;

  try {
    history = await read<FeedVersionPage>({
      path: GTFS_VERSIONS_PATH,
      searchParams: { limit: HISTORY_LIMIT },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const versions = history?.items ?? [];
  const live = activeVersion(versions);

  // A `?feed=` that is not an id asks transit-svc nothing — the same rule C109
  // applies to a mistyped `?id=`. It falls back to the default selection rather
  // than erroring, because the operator most likely edited the URL by hand.
  const selectedId = selectedFeedId(versions, isFeedVersionId(requested) ? requested : undefined);
  const selectedRow = versionRow(versions, selectedId);

  let status: FeedUploadStatus | null = null;
  let statusProblem: ProblemDetails | null = null;

  if (selectedId) {
    try {
      status = await read<FeedUploadStatus>({ path: gtfsUploadPath(selectedId) });
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      // The card is what fails, not the screen: the history is the half of this
      // page an operator can still act on, and it has already been read.
      statusProblem = error.problem;
    }
  }

  const feed = status ? feedCardView(status, context) : null;
  const liveLabel = live ? feedLabel(live) : null;
  const liveRoutes = live ? routeCount(live.counts, context) : null;

  const activateLabels = {
    open: t('admin.transit.activate.open'),
    reactivate: t('admin.transit.activate.reactivate'),
    title: t('admin.transit.activate.title'),
    replacing: t('admin.transit.activate.replacing'),
    firstFeed: t('admin.transit.activate.firstFeed'),
    atomic: t('admin.transit.activate.atomic'),
    rollbackNote: t('admin.transit.activate.rollbackNote'),
    confirm: t('admin.transit.activate.confirm'),
    cancel: t('common.cancel'),
    close: t('common.close'),
    working: t('admin.transit.activate.working'),
    done: t('admin.transit.activate.done'),
    reload: t('admin.transit.activate.reload'),
    dismiss: t('admin.transit.activate.dismiss'),
    audit: t('admin.audit.notice'),
  };

  return (
    // One provider for the screen. Every activatable history row draws an
    // `ActivateForm`, and after a few years of refreshes most of a hundred-row
    // history is archived and therefore activatable — a provider each would pin a
    // hundred viewports to the same corner.
    <ToastProvider>
      <div className="flex flex-col gap-md">
        <ScreenTabs
          navLabel={t('admin.config.tabs.label')}
          tabs={tabs.map((tab) => ({
            id: tab.id,
            href: tab.href,
            label: t(tab.labelKey),
            current: tab.current,
          }))}
        />

        {/*
          The wireframe's topbar pill. "Which dataset is answering passenger route
          queries right now" is the one fact this screen must never make somebody
          hunt for — and `—` where nothing is live is AL-55's day-0 state stated
          rather than left blank.
        */}
        <div className="flex flex-wrap items-center gap-sm">
          <StatusPill tone={live ? 'success' : 'warning'}>
            {live
              ? t('admin.transit.live', {
                  version: liveLabel ?? '',
                  routes: liveRoutes ?? t('admin.transit.unknownCount'),
                })
              : t('admin.transit.noLive')}
          </StatusPill>
        </div>

        {problem ? <ProblemPanel problem={problem} /> : null}

        {/* AL-55's pre-first-import state: nothing has ever been uploaded. */}
        {!problem && versions.length === 0 ? (
          <section className="flex flex-col gap-xs rounded-card border border-outline bg-background p-lg shadow-card">
            <h2 className="text-title font-display">{t('admin.transit.empty.title')}</h2>
            <p className="text-body-sm text-on-surface-variant">{t('admin.transit.empty.body')}</p>
          </section>
        ) : null}

        <div className="flex flex-col gap-md lg:flex-row lg:items-start">
          <div className="lg:w-[340px] lg:shrink-0">
            <UploadCard
              screenPath={SCREEN_PATH}
              labels={{
                heading: t('admin.transit.upload.heading'),
                dropzone: t('admin.transit.upload.dropzone'),
                hint: t('admin.transit.upload.hint'),
                required: t('admin.transit.upload.required'),
                externalNote: t('admin.transit.upload.externalNote'),
                uploading: t('admin.transit.upload.uploading'),
                percent: t('admin.transit.upload.percent'),
                cancel: t('admin.transit.upload.cancel'),
                rejectedType: t('admin.transit.upload.rejectedType'),
                rejectedSize: t('admin.transit.upload.rejectedSize'),
                rejectedCount: t('admin.transit.upload.rejectedCount'),
                duplicate: t('admin.transit.upload.duplicate'),
                duplicateOpen: t('admin.transit.upload.duplicateOpen'),
                sessionEnded: t('admin.error.unauthorized'),
                failed: t('admin.transit.upload.failed'),
                audit: t('admin.audit.notice'),
              }}
            />
          </div>

          <div className="min-w-0 flex-1">
            {statusProblem ? <ProblemPanel problem={statusProblem} /> : null}

            {feed ? (
              <FeedCard
                feed={feed}
                // Never itself: re-activating the live feed is refused, and a dialog
                // saying "version 7 replaces version 7" would be nonsense on the one
                // row where it could be drawn.
                outgoing={live && live.feedVersionId !== feed.feedVersionId ? liveLabel : null}
                reportHref={`${REPORT_PATH}/${feed.feedVersionId}`}
                labels={{
                  heading: selectedRow
                    ? t('admin.transit.preview.headingFile', { file: selectedRow.fileName })
                    : t('admin.transit.preview.heading'),
                  stepperLabel: t('admin.transit.stepper.label'),
                  version: t('admin.transit.preview.version'),
                  noVersion: t('admin.transit.preview.noVersion'),
                  serviceWindow: t('admin.transit.preview.serviceWindow'),
                  noWindow: t('admin.transit.preview.noWindow'),
                  countsCaption: t('admin.transit.preview.countsCaption'),
                  noCounts: t('admin.transit.preview.noCounts'),
                  validatingNote: t('admin.transit.preview.validatingNote'),
                  noWarnings: t('admin.transit.preview.noWarnings'),
                  failedHeading: t('admin.transit.failed.heading'),
                  failedBody: t('admin.transit.failed.body'),
                  reportCsv: t('admin.transit.failed.reportCsv'),
                  reportJson: t('admin.transit.failed.reportJson'),
                  liveNote: t('admin.transit.preview.liveNote'),
                  archivedNote: t('admin.transit.preview.archivedNote'),
                  activate: activateLabels,
                }}
              />
            ) : null}
          </div>
        </div>

        {problem ? null : (
          <VersionHistory
            rows={versionRows(versions, selectedId, context)}
            outgoing={liveLabel}
            screenPath={SCREEN_PATH}
            reportPath={REPORT_PATH}
            zipPath={ZIP_PATH}
            capped={history?.hasMore ?? false}
            labels={{
              heading: t('admin.transit.history.heading'),
              caption: t('admin.transit.history.caption'),
              version: t('admin.transit.history.version'),
              file: t('admin.transit.history.file'),
              uploaded: t('admin.transit.history.uploaded'),
              routes: t('admin.transit.history.routes'),
              status: t('admin.transit.history.status'),
              actions: t('admin.transit.history.actions'),
              empty: t('admin.transit.history.empty'),
              report: t('admin.transit.history.report'),
              zip: t('admin.transit.history.zip'),
              capped: t('admin.transit.history.capped', { limit: HISTORY_LIMIT }),
              none: t('admin.transit.none'),
              activate: activateLabels,
            }}
          />
        )}
      </div>
    </ToastProvider>
  );
}

/** A query parameter Next may hand back as an array. */
function first(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
