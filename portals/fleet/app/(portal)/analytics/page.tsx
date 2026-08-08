import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import {
  analyticsRange,
  colomboToday,
  DEFAULT_ANALYTICS_DAYS,
  FLEET_ANALYTICS,
  isBusinessDate,
  MAX_ANALYTICS_DAYS,
  rangeProblem,
  type FleetAnalyticsAnswer,
} from '@/api/insights';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { KpiTiles } from '@/components/KpiTiles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { AnalyticsRangeForm } from '@/components/analytics/AnalyticsRangeForm';
import { AnalyticsTable } from '@/components/analytics/AnalyticsTable';
import { ExportControls } from '@/components/analytics/ExportControls';
import { analyticsRow, analyticsSummary } from '@/components/analytics/analytics-model';
import { formatDay } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-009 · `fleet_analytics`** — trip history and analytics (US-13.4).
 *
 * ## The period is the URL, and the URL is what the CSV is handed
 *
 * `?from=&to=` are `_shared.yaml` `BusinessDate`s, evaluated by fleet-svc in
 * **Asia/Colombo** (D-13): "a date range an operator types is local days — '1st to
 * 7th' means the seven days their drivers worked, not seven UTC days starting five
 * and a half hours late". They are sent explicitly rather than left to the
 * service's own defaults, because the caption has to name the period the figures
 * cover and a caption nobody can check is worse than none.
 *
 * The export route reads the same two values, so the file and the screen are the
 * same report by construction rather than by care.
 *
 * ## Open to a Viewer, and open to a pending organisation
 *
 * `GET …/analytics` is `RequireFleetSubRole(Viewer)` on a group carrying no
 * approval gate — URD §2.1 gives the Viewer "read-only fleet map & analytics" in
 * as many words, and this screen renders no mutating control for anybody, so there
 * is nothing on it to gate further. The **roster** read beside it is
 * approval-gated, and its failure costs the Type column and nothing else.
 *
 * ## What the report cannot say
 *
 * **Earnings.** `VehicleAnalytics.earningsMinor` is offered by the contract and
 * fleet-svc returns it absent on purpose: a fleet's Mode A/B vehicles take no
 * fares on this platform, so "zero would be a claim that the operator earned
 * nothing; absent is the truth, which is that this platform does not know"
 * (BR-23.10). There is no earnings column, and the table says why rather than
 * printing a column of dashes.
 *
 * **Road distance.** The kilometres are great-circle hops between telemetry
 * samples; nothing in this build map-matches a completed journey (C059 handoff).
 * The caption says so, because a number that looks like an odometer reading will
 * be reconciled against one.
 */

export const dynamic = 'force-dynamic';

export default async function AnalyticsPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale, query] = await Promise.all([getTranslator(), getLocale(), searchParams]);

  const today = colomboToday();
  const asked = { from: single(query['from']), to: single(query['to']) };
  const range = analyticsRange(asked, today);

  // Said out loud when the report is not the range that was typed. Falling back
  // silently would leave an operator reading thirty days of figures under a
  // heading they thought said three months.
  const adjusted =
    isBusinessDate(asked.from) && rangeProblem(asked.from, range.to) !== null
      ? t('fleet.analytics.rangeAdjusted', { days: MAX_ANALYTICS_DAYS })
      : null;

  let answer: FleetAnalyticsAnswer | null = null;
  let problem: ProblemDetails | null = null;
  try {
    answer = await read<FleetAnalyticsAnswer>({
      org: FLEET_ANALYTICS,
      searchParams: { from: range.from, to: range.to },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const roster = await rosterById();
  const items = answer?.items ?? [];
  const summary = analyticsSummary(items, range, locale, t);

  const period = t('fleet.analytics.period', {
    from: formatDay(locale, `${range.from}T00:00:00Z`) ?? range.from,
    to: formatDay(locale, `${range.to}T00:00:00Z`) ?? range.to,
    days: range.days,
  });

  const csvHref = `/analytics/export?from=${range.from}&to=${range.to}`;

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">{t('fleet.analytics.title')}</h2>
        <ExportControls
          csvHref={csvHref}
          labels={{ csv: t('fleet.analytics.exportCsv'), pdf: t('fleet.analytics.exportPdf') }}
        />
      </div>

      <AnalyticsRangeForm
        range={range}
        today={today}
        labels={{
          legend: t('fleet.analytics.range.legend'),
          from: t('fleet.analytics.range.from'),
          to: t('fleet.analytics.range.to'),
          apply: t('fleet.analytics.range.apply'),
          hint: t('fleet.analytics.range.hint', {
            days: DEFAULT_ANALYTICS_DAYS,
            max: MAX_ANALYTICS_DAYS,
          }),
          adjusted,
        }}
      />

      <p className="text-body-sm text-on-surface-variant">{period}</p>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <KpiTiles
        tiles={[
          {
            key: 'trips',
            label: t('fleet.analytics.kpi.trips'),
            value: summary.trips,
          },
          {
            key: 'distance',
            label: t('fleet.analytics.kpi.distance'),
            value: summary.distance,
          },
          {
            key: 'utilisation',
            label: t('fleet.analytics.kpi.utilisation'),
            value: summary.utilisation,
            detail: t('fleet.analytics.kpi.utilisationDetail', {
              vehicles: summary.totals.vehicles,
            }),
          },
          {
            key: 'idle',
            label: t('fleet.analytics.kpi.idle'),
            value: summary.idlePerDay,
            detail: t('fleet.analytics.kpi.idleDetail'),
          },
        ]}
      />

      <AnalyticsTable
        rows={items.map((item) => analyticsRow(item, range, roster, locale, t))}
        labels={{
          heading: t('fleet.analytics.table.heading'),
          caption: t('fleet.analytics.table.caption'),
          vehicle: t('fleet.analytics.column.vehicle'),
          trips: t('fleet.analytics.column.trips'),
          distance: t('fleet.analytics.column.distance'),
          utilisation: t('fleet.analytics.column.utilisation'),
          idle: t('fleet.analytics.column.idle'),
          empty: t('fleet.analytics.table.empty'),
          idleNote: t('fleet.analytics.idleNote'),
          distanceNote: t('fleet.analytics.distanceNote'),
          earningsNote: t('fleet.analytics.earningsNote'),
        }}
      />
    </div>
  );
}

/** The first value of a repeated query parameter, or `undefined`. */
function single(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}

/**
 * The roster, by id, for the Type column and the CSV's type and mode.
 *
 * Best effort: `FleetVehiclesGroup` carries `RequireApprovedFleet()`, so a pending
 * organisation's analytics render with plates and no types rather than not at all.
 */
async function rosterById(): Promise<ReadonlyMap<string, FleetVehicle>> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return new Map((answer.items ?? []).map((vehicle) => [vehicle.vehicleId, vehicle]));
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return new Map();
  }
}
