import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { DashboardStats } from '@/api/dashboard';
import type { AdminMenuGroup } from '@/api/types';
import { AlertsCard } from '@/components/dashboard/AlertsCard';
import { buildDashboardView } from '@/components/dashboard/model';
import { StatGrid } from '@/components/dashboard/StatGrid';
import { StatsFilter } from '@/components/dashboard/StatsFilter';
import { createAdminTranslator } from '@/i18n';

import { menuFor } from './support/urd';

/**
 * SCR-AP-002 — the role-scoped dashboard, its period filter, and the three rules
 * the screen exists to hold (AL-38, US-24.7):
 *
 *  - **period KPIs recompute, live cards do not.** The filter is the URL, so what
 *    is asserted here is that the two blocks are drawn apart and that the live
 *    tiles carry no vs-previous-period line at all — there is no previous period
 *    for a fact about this second (D6' §I-28.5).
 *  - **an absent delta is not a zero.** C061 answers `null` when the previous
 *    period was empty, and the card has to say "no comparison" rather than "no
 *    change".
 *  - **an alert links somewhere only if the operator's menu says so.** The count
 *    reaches every permitted role; the queue does not (AL-06).
 */

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');

/** The wireframe's own figures, in the contract's shape. */
const STATS: DashboardStats = {
  period: 'month',
  range: { from: '2026-06-01', to: '2026-06-28' },
  kpis: {
    completedTrips: 48_210,
    grossFareMinor: 3_860_000_000,
    newRiders: 2_140,
    newDrivers: 318,
    dailyFeeRevenueMinor: 590_000_000,
  },
  deltaVsPrev: {
    completedTripsPct: 9,
    grossFarePct: 7,
    newRidersPct: 12,
    // The case the whole `unknown` direction exists for: the previous period had
    // no new drivers, so there is no percentage.
    newDriversPct: null,
    dailyFeeRevenuePct: -3.5,
  },
  live: { onlineDrivers: 1_204, pendingVerifications: 38, openTickets: 57 },
};

function view(stats: DashboardStats = STATS, menu: readonly AdminMenuGroup[] = menuFor(['admin'])) {
  return buildDashboardView(stats, menu, t, 'en');
}

/** A menu carrying exactly the named nav items, and nothing else. */
function menuWith(...keys: readonly string[]): AdminMenuGroup[] {
  return [
    {
      key: 'overview',
      labelKey: 'nav.group.overview',
      items: keys.map((key) => ({
        key,
        labelKey: 'nav.dashboard',
        path: `/${key}`,
        ownedBy: 'admin-bff',
      })),
    },
  ];
}

describe('the period block', () => {
  it('draws the wireframe’s four tiles from the contract’s five figures', () => {
    expect(view().period.map((stat) => stat.key)).toEqual([
      'completedTrips',
      'grossFare',
      'newRidersDrivers',
      'dailyFeeRevenue',
    ]);
  });

  it('renders money from integer minor units, with the mark as a resource string', () => {
    const [, grossFare] = view().period;

    // Rs 38,600,000 — the wireframe's "38.6M", unabbreviated.
    expect(grossFare?.value).toBe('Rs 38,600,000');
  });

  it('keeps the riders/drivers tile whole and gives each half its own delta', () => {
    const card = view().period.find((stat) => stat.key === 'newRidersDrivers');

    expect(card?.value).toBe('2,140 / 318');
    expect(card?.deltas.map((delta) => delta.qualifier)).toEqual(['riders', 'drivers']);
  });

  it('signs a delta by direction, unsigned, with the arrow carrying the sign', () => {
    const byKey = new Map(view().period.map((stat) => [stat.key, stat.deltas[0]]));

    expect(byKey.get('completedTrips')).toMatchObject({ direction: 'up', display: '▲ 9%' });
    expect(byKey.get('dailyFeeRevenue')).toMatchObject({ direction: 'down', display: '▼ 3.5%' });
  });

  it('says “no comparison” rather than 0% when the previous period was empty', () => {
    const drivers = view()
      .period.find((stat) => stat.key === 'newRidersDrivers')
      ?.deltas.find((delta) => delta.key === 'newDrivers');

    expect(drivers?.direction).toBe('unknown');
    expect(drivers?.display).toBe('—');
    expect(drivers?.description).toContain('no comparison');
  });

  it('treats a zero delta as a real comparison that found no change', () => {
    const flat = view({ ...STATS, deltaVsPrev: { ...STATS.deltaVsPrev, grossFarePct: 0 } }).period.find(
      (stat) => stat.key === 'grossFare',
    );

    expect(flat?.deltas[0]).toMatchObject({ direction: 'flat', display: '0%' });
  });

  it('refuses a NaN or an Infinity the same way it refuses a null', () => {
    const broken = view({
      ...STATS,
      deltaVsPrev: { ...STATS.deltaVsPrev, completedTripsPct: Number.POSITIVE_INFINITY },
    });

    expect(broken.period[0]?.deltas[0]?.direction).toBe('unknown');
  });
});

describe('the live block', () => {
  it('is the contract’s three real-time counters', () => {
    expect(view().live.map((stat) => [stat.key, stat.value])).toEqual([
      ['onlineDrivers', '1,204'],
      ['pendingVerifications', '38'],
      ['openTickets', '57'],
    ]);
  });

  it('carries no vs-previous-period line, because there is no previous period', () => {
    // The filter changing must leave these three where they are (AL-38); a delta
    // on one would be the screen claiming the period moved them.
    expect(view().live.every((stat) => stat.deltas.length === 0)).toBe(true);
  });
});

describe('the alerts feed', () => {
  it('lists only the queues with work waiting in them', () => {
    const clear = view({ ...STATS, live: { ...STATS.live, pendingVerifications: 0, openTickets: 0 } });
    expect(clear.alerts).toEqual([]);

    const half = view({ ...STATS, live: { ...STATS.live, openTickets: 0 } });
    expect(half.alerts.map((alert) => alert.key)).toEqual(['pendingVerifications']);
  });

  it('links a row to the screen the caller’s own menu carries', () => {
    const alerts = view(STATS, menuWith('verification')).alerts;

    expect(alerts.find((alert) => alert.key === 'pendingVerifications')?.href).toBe('/verification');
    // The count still reaches them; the queue is not theirs to open, and a link
    // this portal drew itself would be one `proxy.ts` then refuses.
    expect(alerts.find((alert) => alert.key === 'openTickets')?.href).toBeUndefined();
  });

  it('agrees with the menu admin-bff would actually send a Support/CSR', () => {
    // Built from the URD's own §2.3 table and `AdminMenu.cs`, not from a fixture.
    const alerts = view(STATS, menuFor(['support_csr'])).alerts;

    expect(alerts.map((alert) => alert.href)).toEqual(['/verification', '/support/tickets']);
  });
});

describe('what a screen reader is given', () => {
  it('hides the glyph and reads the sentence, naming the metric', () => {
    render(<StatGrid heading="Period" stats={view().period} />);

    const trips = screen.getByText('Completed trips').closest('div');
    expect(within(trips as HTMLElement).getByText(/Completed trips: up 9% on the previous period/)).
      toBeTruthy();
    // The visible `▲ 9%` announces as "up-pointing triangle nine percent" under a
    // heading the reader has already left behind — so it is not announced at all.
    expect(within(trips as HTMLElement).getByText('▲ 9%').getAttribute('aria-hidden')).toBe('true');
  });

  it('says how many are waiting rather than leaving a bare number', () => {
    render(
      <AlertsCard heading="Needs attention" caption="Needs attention" emptyLabel="Clear" alerts={view().alerts} />,
    );

    expect(screen.getByText('38 waiting')).toBeTruthy();
  });
});

describe('the alerts card', () => {
  it('draws a row as a link only where the model gave it one', () => {
    render(
      <AlertsCard
        heading="Needs attention"
        caption="Needs attention"
        emptyLabel="Clear"
        alerts={view(STATS, menuWith('verification')).alerts}
      />,
    );

    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(1);
    expect(links[0]?.getAttribute('href')).toBe('/verification');
    // The row is still there — the operator can see the count, just not open it.
    expect(screen.getByText('Support tickets still open')).toBeTruthy();
  });

  it('says so when there is nothing waiting, rather than drawing an empty table', () => {
    render(
      <AlertsCard heading="Needs attention" caption="Needs attention" emptyLabel="All clear" alerts={[]} />,
    );

    expect(screen.getByText('All clear')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });
});

describe('the filter bar', () => {
  const LABELS = {
    legend: 'Statistics for',
    today: 'Today',
    week: 'This week',
    month: 'This month',
    custom: 'Custom range',
    from: 'From',
    to: 'To',
    apply: 'Apply',
    comparison: 'vs previous period',
    export: 'Export CSV',
    timezone: 'Asia/Colombo',
  };

  function renderFilter(
    selection: Parameters<typeof StatsFilter>[0]['selection'],
    canExport = true,
  ) {
    return render(
      <StatsFilter
        selection={selection}
        rangeLabel="1 – 28 Jun 2026"
        canExport={canExport}
        labels={LABELS}
      />,
    );
  }

  it('is four links and no state, so the filter survives a reload and a bookmark', () => {
    renderFilter({ period: 'month', awaitingRange: false });

    expect(screen.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual([
      '/dashboard?period=today',
      '/dashboard?period=week',
      '/dashboard?period=month',
      '/dashboard?period=custom',
    ]);
  });

  it('marks the chosen period, and only that one', () => {
    renderFilter({ period: 'week', awaitingRange: false });

    const current = screen
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.textContent).toBe('This week');
  });

  it('holds the chosen dates on the custom link and drops them from the fixed three', () => {
    renderFilter({ period: 'custom', from: '2026-06-01', to: '2026-06-28', awaitingRange: false });

    const hrefs = screen.getAllByRole('link').map((link) => link.getAttribute('href'));

    // The three fixed windows are admin-bff's to resolve; a `from` on their URL
    // would be a range the figures underneath do not reflect.
    expect(hrefs.slice(0, 3)).toEqual([
      '/dashboard?period=today',
      '/dashboard?period=week',
      '/dashboard?period=month',
    ]);
    expect(hrefs.at(-1)).toBe('/dashboard?period=custom&from=2026-06-01&to=2026-06-28');
  });

  it('shows the date form only on a custom range', () => {
    const { unmount } = renderFilter({ period: 'today', awaitingRange: false });
    expect(screen.queryByLabelText('From')).toBeNull();
    unmount();

    renderFilter({ period: 'custom', awaitingRange: true }, false);
    expect(screen.getByLabelText('From')).toBeTruthy();
    expect(screen.getByLabelText('To')).toBeTruthy();
  });

  it('offers no export while the screen is showing no figures', () => {
    // "The CSV contains exactly the filtered figures on screen" — with none on
    // screen, the honest button is no button.
    renderFilter({ period: 'custom', awaitingRange: true }, false);
    expect(screen.queryByRole('button', { name: 'Export CSV' })).toBeNull();
  });

  it('exports through the same query the page was rendered with', () => {
    const { container } = renderFilter({
      period: 'custom',
      from: '2026-06-01',
      to: '2026-06-28',
      awaitingRange: false,
    });

    const form = container.querySelector('form[action="/dashboard/export"]');
    const sent = Object.fromEntries(new FormData(form as HTMLFormElement).entries());

    expect(sent).toEqual({ period: 'custom', from: '2026-06-01', to: '2026-06-28' });
  });
});
