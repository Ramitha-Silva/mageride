import {
  STATS_PATH,
  statsSearch,
  statsSelection,
  type DashboardStats,
} from '@/api/dashboard';
import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { AlertsCard } from '@/components/dashboard/AlertsCard';
import { buildDashboardView } from '@/components/dashboard/model';
import { StatGrid } from '@/components/dashboard/StatGrid';
import { StatsFilter } from '@/components/dashboard/StatsFilter';
import { ProblemPanel } from '@/components/ProblemPanel';
import { formatDateRange } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-002 · `admin_home`** — the role-scoped dashboard and its statistics
 * period filter (AL-38, US-24.7).
 *
 * ## What the filter does, and what it deliberately leaves alone
 *
 * `?period=today|week|month|custom&from&to` is the whole state. The four period
 * KPIs and their vs-previous-period deltas are re-read on every change; the three
 * live cards — online drivers, pending verifications, open tickets — are on the
 * same payload and describe **this instant**, so filtering to last March must
 * leave them exactly where they are. That is D6' §I-28.5's rule and C061 holds it
 * server-side; what this screen contributes is drawing the two blocks apart, under
 * their own headings, so a filter that visibly moves five figures and not three
 * reads as designed rather than broken.
 *
 * ## Why the page has no RBAC branch
 *
 * There is no `if (role === …)` here and there must not be. This screen is
 * reachable iff `dashboard` is in the menu `GET /v1/admin/session` already
 * filtered through the same evaluator admin-bff's own endpoints gate on — the
 * proxy has decided that before this file runs. The one place the session is read
 * is the alerts feed, and not to decide what may be *seen*: it decides which rows
 * are **links**, because a row pointing at a queue the operator's menu does not
 * carry would be a link the proxy then refuses.
 *
 * ## The export is the same request
 *
 * `/dashboard/export` carries the query this page was rendered with, and
 * admin-bff renders `stats.csv` from the same service call as `stats`. So "the CSV
 * contains exactly the filtered figures on screen" is structural rather than a
 * coincidence two code paths share — and the button is not drawn at all when the
 * screen is showing no figures.
 */

export const dynamic = 'force-dynamic';

export default async function DashboardPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const selection = statsSelection(await searchParams);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);

  let stats: DashboardStats | null = null;
  let problem: ProblemDetails | null = null;

  // Nothing is asked of admin-bff while a custom range is half-chosen: the
  // alternative is answering the operator's first click on "Custom range" with a
  // validation error about a form they have not filled in yet.
  if (!selection.awaitingRange) {
    try {
      stats = await read<DashboardStats>({
        path: STATS_PATH,
        searchParams: statsSearch(selection),
      });
    } catch (error) {
      // A refused or impossible range is the operator's to correct, and the filter
      // has to stay on screen for them to correct it. Anything that is not a
      // problem+json failure is the error boundary's.
      if (!(error instanceof ProblemError)) throw error;
      problem = error.problem;
    }
  }

  const view = stats ? buildDashboardView(stats, session?.menu ?? [], t, locale) : null;

  return (
    <div className="flex flex-col gap-lg">
      <StatsFilter
        selection={selection}
        rangeLabel={stats ? formatDateRange(locale, stats.range.from, stats.range.to) : null}
        canExport={view !== null}
        labels={{
          legend: t('admin.dashboard.filter.legend'),
          today: t('admin.dashboard.filter.today'),
          week: t('admin.dashboard.filter.week'),
          month: t('admin.dashboard.filter.month'),
          custom: t('admin.dashboard.filter.custom'),
          from: t('admin.dashboard.filter.from'),
          to: t('admin.dashboard.filter.to'),
          apply: t('admin.dashboard.filter.apply'),
          comparison: t('admin.dashboard.filter.comparison'),
          export: t('admin.dashboard.filter.export'),
          timezone: t('admin.dashboard.filter.timezone'),
        }}
      />

      {selection.awaitingRange ? (
        <p className="rounded-card border border-outline bg-background p-md text-body-sm text-on-surface-variant shadow-card">
          {t('admin.dashboard.filter.chooseRange')}
        </p>
      ) : null}

      {problem ? <ProblemPanel problem={problem} /> : null}

      {view ? (
        <>
          <StatGrid heading={t('admin.dashboard.period.heading')} stats={view.period} />

          <StatGrid
            heading={t('admin.dashboard.live.heading')}
            note={t('admin.dashboard.live.note')}
            stats={view.live}
          />

          <AlertsCard
            heading={t('admin.dashboard.alerts.heading')}
            caption={t('admin.dashboard.alerts.heading')}
            emptyLabel={t('admin.dashboard.alerts.clear')}
            alerts={view.alerts}
          />
        </>
      ) : null}
    </div>
  );
}
