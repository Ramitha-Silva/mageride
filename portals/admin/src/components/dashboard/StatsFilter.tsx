'use client';

import Link from 'next/link';

import { Button, Field, Input, StatusPill } from '@mageride/ui';

import {
  STATS_PERIODS,
  statsHref,
  statsSearch,
  type StatsPeriod,
  type StatsSelection,
} from '@/api/dashboard';

/**
 * SCR-AP-002's statistics filter (AL-38, US-24.7): Today · This week · This month
 * · Custom range, the window it resolved to, and the export of exactly what is on
 * screen.
 *
 * **It is a client component for one reason — `Field` and `Input` use hooks** —
 * and it holds no state whatsoever. The four periods are *links*, the custom range
 * is a plain `method="get"` form, and the export is a second one: the filter is a
 * property of the URL, so it survives a reload, a bookmark, a pasted link in a
 * ticket and a browser with no JavaScript, and the back button steps through an
 * operator's comparisons the way they expect. Holding it in React state would give
 * up all of that to avoid a navigation the server has to do anyway.
 *
 * Every string arrives translated, as a prop — `SignInForm`'s rule, for
 * `SignInForm`'s reason: importing the translator here would ship all three locale
 * tables to the browser so one filter bar could look up eleven words.
 *
 * **Export is drawn only when there are figures to export.** The DoD asks that the
 * CSV contain exactly the filtered figures on screen; when the screen has none —
 * a custom range still being chosen, or a query admin-bff refused — the honest
 * button is no button.
 */

export interface StatsFilterLabels {
  readonly legend: string;
  readonly today: string;
  readonly week: string;
  readonly month: string;
  readonly custom: string;
  readonly from: string;
  readonly to: string;
  readonly apply: string;
  readonly comparison: string;
  readonly export: string;
  readonly timezone: string;
}

const LEGEND_ID = 'admin-stats-filter-legend';

const PERIOD_LABEL: Readonly<Record<StatsPeriod, keyof StatsFilterLabels>> = {
  today: 'today',
  week: 'week',
  month: 'month',
  custom: 'custom',
};

export function StatsFilter({
  selection,
  rangeLabel,
  canExport,
  labels,
}: {
  selection: StatsSelection;
  /** The window admin-bff resolved, already formatted. Absent until it has answered. */
  rangeLabel: string | null;
  canExport: boolean;
  labels: StatsFilterLabels;
}) {
  return (
    <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-sm shadow-card">
      <div className="flex flex-wrap items-center gap-x-sm gap-y-xs">
        <p id={LEGEND_ID} className="text-label font-semibold text-on-surface">
          {labels.legend}
        </p>

        <div
          role="group"
          aria-labelledby={LEGEND_ID}
          className="flex flex-wrap items-center gap-xxs rounded-sm border border-outline p-xxs"
        >
          {STATS_PERIODS.map((period) => {
            const current = period === selection.period;
            return (
              <Link
                key={period}
                href={hrefFor(period, selection)}
                aria-current={current ? 'page' : undefined}
                className={`rounded-sm px-sm py-xxs text-label transition-colors ${
                  current
                    ? 'bg-primary text-on-primary'
                    : 'text-on-surface-variant hover:bg-surface-variant'
                }`}
              >
                {labels[PERIOD_LABEL[period]]}
              </Link>
            );
          })}
        </div>

        {rangeLabel ? (
          <p className="text-label text-on-surface-variant">{rangeLabel}</p>
        ) : null}

        <span className="flex-1" />

        <StatusPill tone="neutral" dot={false}>
          {labels.comparison}
        </StatusPill>

        {canExport ? (
          <form method="get" action="/dashboard/export">
            {Object.entries(statsSearch(selection)).map(([name, value]) => (
              <input key={name} type="hidden" name={name} value={value} />
            ))}
            <Button type="submit" size="compact" variant="secondary">
              {labels.export}
            </Button>
          </form>
        ) : null}
      </div>

      {selection.period === 'custom' ? (
        <form method="get" action="/dashboard" className="flex flex-wrap items-end gap-xs">
          <input type="hidden" name="period" value="custom" />

          <Field label={labels.from} className="w-[170px]">
            <Input type="date" name="from" defaultValue={selection.from ?? ''} required />
          </Field>
          <Field label={labels.to} className="w-[170px]">
            <Input type="date" name="to" defaultValue={selection.to ?? ''} required />
          </Field>

          <Button type="submit" size="compact">
            {labels.apply}
          </Button>

          <p className="w-full text-caption text-on-surface-variant">{labels.timezone}</p>
        </form>
      ) : null}
    </section>
  );
}

/**
 * Switching to `custom` keeps the dates already chosen, so an operator comparing
 * a range against this month can go back to it without retyping. The three fixed
 * periods carry no range: admin-bff resolves theirs, and a `from` in the URL that
 * the figures do not reflect is a URL somebody later reads as evidence.
 */
function hrefFor(period: StatsPeriod, selection: StatsSelection): string {
  if (period !== 'custom') return statsHref('/dashboard', { period, awaitingRange: false });

  return statsHref('/dashboard', {
    period: 'custom',
    ...(selection.from ? { from: selection.from } : {}),
    ...(selection.to ? { to: selection.to } : {}),
    awaitingRange: selection.awaitingRange,
  });
}
