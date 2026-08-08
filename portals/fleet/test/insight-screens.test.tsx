import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Assignment } from '@/api/drivers';
import type { FleetVehiclePosition, VehicleAnalytics } from '@/api/insights';
import { ProblemError } from '@/api/problem';
import type { FleetHealthRollup, TrackerHealth } from '@/api/trackers';
import type { FleetVehicle } from '@/api/vehicles';
import type { FleetWallet, FleetInvoice } from '@/api/billing';
import { createFleetTranslator } from '@/i18n';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-003, SCR-FP-007 and SCR-FP-009 on the rendered page** — this
 * component's Definition of Done, where an operator would meet it.
 *
 * Five items cannot be checked anywhere else:
 *
 *  1. the map is handed **only** the caller's own organisation's vehicles, and a
 *     vehicle id from anywhere else resolves to nothing rather than to a marker;
 *  2. the health overlay follows Online → Stale → Offline, including the case the
 *     two windows disagree about (offline in the table, no pin on the map);
 *  3. the analytics tiles reconcile with the rows underneath them;
 *  4. the wallet card is an Owner's, and is a sentence for everybody else;
 *  5. no screen names an on-demand mode, and none of them draws a control the
 *     platform has no route for.
 */

const redirected = new Error('NEXT_REDIRECT');

const redirect = vi.fn(() => {
  throw redirected;
});
const getSession = vi.fn();
const read = vi.fn();

/** What the (mocked) MapLibre component was handed on the last render. */
const mapProps = vi.fn();

vi.mock('next/navigation', () => ({
  redirect: () => redirect(),
  permanentRedirect: () => redirect(),
  forbidden: () => {
    throw new Error('NEXT_FORBIDDEN');
  },
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  usePathname: () => '/map',
  useSearchParams: () => new URLSearchParams(),
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/api/client', () => ({ read: (options: unknown) => read(options), mutate: vi.fn() }));
vi.mock('@/config/env', () => ({ mapStyleUrl: () => null }));

// MapLibre needs a WebGL context, which jsdom has none of. The component's own
// job — turning positions into a GeoJSON layer — is not what these tests are
// about; what they check is *which* vehicles the page hands it.
vi.mock('@/components/map/FleetMap', () => ({
  FleetMap: (props: unknown) => {
    mapProps(props);
    return <div data-testid="fleet-map" />;
  },
}));

const { default: DashboardPage } = await import('../app/(portal)/dashboard/page');
const { default: MapPage } = await import('../app/(portal)/map/page');
const { default: AnalyticsPage } = await import('../app/(portal)/analytics/page');

const t = createFleetTranslator('en');

/* ---------------------------------------------------------------------------
 * Fixtures — one organisation's fleet
 * ------------------------------------------------------------------------ */

const BUS = '01JQV000000000000000000001';
const VAN = '01JQV000000000000000000002';
const COACH = '01JQV000000000000000000003';

/** A vehicle id belonging to a different organisation. Nothing answers for it. */
const OTHER_ORG_VEHICLE = '01JQZ999999999999999999999';

const ROSTER: { items: FleetVehicle[] } = {
  items: [
    { vehicleId: BUS, registrationNumber: 'NB-4521', vehicleType: 'bus', mode: 'A', status: 'APPROVED', docsStatus: 'docs_complete' },
    { vehicleId: VAN, registrationNumber: 'VN-8810', vehicleType: 'van', mode: 'B', status: 'APPROVED', docsStatus: 'docs_pending' },
    { vehicleId: COACH, registrationNumber: 'NC-1200', vehicleType: 'bus', mode: 'A', status: 'PENDING', docsStatus: 'docs_pending' },
  ],
};

const POSITIONS: FleetVehiclePosition[] = [
  { vehicleId: BUS, registrationNumber: 'NB-4521', lat: 6.9271, lng: 79.8612, heading: 214, speedMps: 9.4, sampleTs: '2026-06-17T04:30:00Z' },
  { vehicleId: VAN, registrationNumber: 'VN-8810', lat: 6.8649, lng: 79.8997, heading: 12, speedMps: 0, sampleTs: '2026-06-17T04:29:00Z' },
];

function tracker(vehicleId: string, state: TrackerHealth['state'], extra: Partial<TrackerHealth> = {}): TrackerHealth {
  return { vehicleId, state, online: state === 'online', ...extra };
}

function health(items: TrackerHealth[], overrides: Partial<FleetHealthRollup> = {}): FleetHealthRollup {
  const counts = {
    online: items.filter((item) => item.state === 'online').length,
    stale: items.filter((item) => item.state === 'stale').length,
    offline: items.filter((item) => item.state === 'offline').length,
    decommissioned: items.filter((item) => item.state === 'decommissioned').length,
    total: items.length,
  };

  return {
    fleetId: '01JQF0000000000000000000FL',
    vehiclesOnline: counts.online,
    vehiclesOffline: counts.stale + counts.offline,
    counts,
    thresholds: { staleAfterSeconds: 300, offlineAfterSeconds: 1800 },
    items,
    itemsTruncated: false,
    asOf: '2026-06-17T04:30:00Z',
    ...overrides,
  };
}

const HEALTHY = health([
  tracker(BUS, 'online', { battery: 62 }),
  tracker(VAN, 'stale', { batteryMv: 3900 }),
  tracker(COACH, 'offline'),
]);

const ASSIGNMENTS: { items: Assignment[] } = {
  items: [
    { assignmentId: 'a1', driverId: 'd1', vehicleId: BUS, driverName: 'K. Fernando', from: '2026-01-01T00:00:00Z', active: true },
    { assignmentId: 'a2', driverId: 'd2', vehicleId: VAN, driverName: 'S. Bandara', from: '2026-01-01T00:00:00Z', active: false },
  ],
};

const ANALYTICS: { items: VehicleAnalytics[] } = {
  items: [
    { vehicleId: BUS, registrationNumber: 'NB-4521', tripCount: 318, distanceKm: 4120, activeHours: 240, utilisationPct: 33.3 },
    { vehicleId: VAN, registrationNumber: 'VN-8810', tripCount: 96, distanceKm: 2010, activeHours: 120, utilisationPct: 16.7 },
  ],
};

const WALLET: FleetWallet = {
  balanceMinor: 8_450_000,
  outstandingMinor: 6_900_000,
  availableMinor: 1_550_000,
  currency: 'LKR',
  movements: [],
};

const INVOICES: { items: FleetInvoice[]; cursor: null; hasMore: boolean } = {
  items: [
    { invoiceId: 'i1', periodMonth: '2026-06-01', amountMinor: 6_900_000, currency: 'LKR', status: 'DUE', vehicleCount: 230, dueAt: '2026-06-15T00:00:00Z' },
    { invoiceId: 'i2', periodMonth: '2026-05-01', amountMinor: 6_600_000, currency: 'LKR', status: 'PAID' },
  ],
  cursor: null,
  hasMore: false,
};

/** Routes each `read({ org })` to its fixture. Anything else is a 404 problem. */
function serve(answers: Readonly<Record<string, unknown>>) {
  read.mockImplementation((options: { org?: string }) => {
    const org = options.org ?? '';
    if (org in answers) {
      const answer = answers[org];
      return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
    }
    return Promise.reject(
      new ProblemError({ type: 'https://mageride.lk/errors/not-found', title: 'x', status: 404 }),
    );
  });
}

const FORBIDDEN = new ProblemError({
  type: 'https://mageride.lk/errors/fleet-not-approved',
  title: 'x',
  status: 403,
});

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);

/* ---------------------------------------------------------------------------
 * SCR-FP-003
 * ------------------------------------------------------------------------ */

describe('SCR-FP-003 · fleet dashboard', () => {
  const FULL = {
    '/health': HEALTHY,
    '/analytics': ANALYTICS,
    '/schedules': { items: [{ scheduleId: 's1', vehicleId: BUS, departAt: '2026-06-17T00:30:00Z', status: 'MISSED' as const }] },
    '/vehicles': ROSTER,
    '/alerts': { items: [], cursor: null, hasMore: false },
    '/wallet': WALLET,
    '/billing': INVOICES,
  };

  it('draws the wireframe’s four counters, in the deployment’s own thresholds', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await DashboardPage());

    expect(screen.getByText(t('fleet.dashboard.kpi.online'))).toBeTruthy();
    expect(screen.getByText(t('fleet.dashboard.kpi.stale'))).toBeTruthy();
    expect(screen.getByText(t('fleet.dashboard.kpi.offline'))).toBeTruthy();
    expect(screen.getByText(t('fleet.dashboard.kpi.trips'))).toBeTruthy();

    // 5 and 30 minutes come off `thresholds`, not out of the source.
    expect(screen.getByText(t('fleet.dashboard.kpi.staleAfter', { minutes: 5 }))).toBeTruthy();
    expect(screen.getByText(t('fleet.dashboard.kpi.offlineAfter', { minutes: 30 }))).toBeTruthy();

    // Two approved vehicles of the three on the roster.
    expect(screen.getByText(t('fleet.dashboard.kpi.ofVehicles', { count: 2 }))).toBeTruthy();
  });

  it('counts today’s trips and splits them by mode from the roster', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await DashboardPage());

    // 318 + 96, split A/B by the roster's own `mode`.
    expect(screen.getByText('414')).toBeTruthy();
    expect(screen.getByText(t('fleet.dashboard.kpi.modeSplit', { a: 318, b: 96 }))).toBeTruthy();
  });

  it('asks for today in Colombo, as a single inclusive day', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    await DashboardPage();

    const analytics = read.mock.calls
      .map(([options]) => options as { org?: string; searchParams?: Record<string, string> })
      .find((options) => options.org === '/analytics');

    expect(analytics?.searchParams?.['from']).toBe(analytics?.searchParams?.['to']);
    expect(analytics?.searchParams?.['from']).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('lists the alerts the platform already raised, and says what it cannot count', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await DashboardPage());

    const alerts = screen.getByRole('region', { name: t('fleet.dashboard.alerts.heading') });

    expect(within(alerts).getByText(t('fleet.dashboard.alert.notStarted'))).toBeTruthy();
    expect(within(alerts).getByText(t('fleet.dashboard.alert.trackerOffline'))).toBeTruthy();
    expect(within(alerts).getByText(t('fleet.dashboard.alert.documentsOutstanding'))).toBeTruthy();

    // US-13.5 is Phase 3 and has no producer — an empty state in words, not a row.
    expect(within(alerts).getByText(t('fleet.dashboard.alerts.phaseThree', { count: 0 }))).toBeTruthy();
    // And the sketch's insurance-expiry row, which no fleet-wide read answers.
    expect(within(alerts).getByText(t('fleet.dashboard.alerts.noExpiryRow'))).toBeTruthy();
  });

  it('raises US-3.16’s device-down banner only for the current window', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    const window = {
      start: '2026-06-17T04:20:00Z',
      end: '2026-06-17T04:25:00Z',
      windowMinutes: 5,
      expectedVehicles: 10,
      reportingVehicles: 4,
      offlineVehicles: 6,
      offlinePct: 60,
      thresholdPct: 30,
      alerting: true,
    };

    serve({ ...FULL, '/health': health(HEALTHY.items as TrackerHealth[], { window }) });
    render(await DashboardPage());

    expect(
      screen.getByText(
        t('fleet.dashboard.alert.deviceDown', {
          offline: 6,
          expected: 10,
          minutes: 5,
          threshold: 30,
        }),
      ),
    ).toBeTruthy();

    cleanup();
    serve({ ...FULL, '/health': health(HEALTHY.items as TrackerHealth[], { window: { ...window, alerting: false } }) });
    render(await DashboardPage());

    expect(screen.queryByText(/device-down/i)).toBeNull();
  });

  it('shows the Owner the wallet and the next invoice to settle', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await DashboardPage());

    const wallet = screen.getByRole('region', { name: t('fleet.dashboard.wallet.heading') });

    expect(within(wallet).getByText('Rs 84,500')).toBeTruthy();
    // Twice: once as what is invoiced and unpaid, once as the invoice itself.
    // With one open month those are the same figure, and both belong on the card.
    expect(within(wallet).getAllByText('Rs 69,000')).toHaveLength(2);
    expect(within(wallet).getByText('Rs 15,500')).toBeTruthy();
    expect(within(wallet).getByText(t('fleet.billing.status.due'))).toBeTruthy();
    expect(within(wallet).getByText(t('fleet.dashboard.wallet.vehicleLines', { count: 230 }))).toBeTruthy();

    // The top-up itself is SCR-FP-010's, so the control is a link to it.
    const topUp = within(wallet).getByText(t('fleet.dashboard.wallet.topUp'));
    expect(topUp.getAttribute('href')).toBe('/billing');
  });

  it('replaces the wallet with a sentence for a Manager, and never reads it', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    render(await DashboardPage());

    expect(screen.getByText(t('fleet.dashboard.wallet.ownerOnly'))).toBeTruthy();

    const billingCalls = read.mock.calls
      .map(([options]) => (options as { org?: string }).org)
      .filter((org) => org === '/wallet' || org === '/billing');

    expect(billingCalls).toEqual([]);
  });

  it('tells a pending organisation’s Owner why there is no invoice yet', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));
    serve({ '/health': HEALTHY, '/analytics': ANALYTICS, '/schedules': { items: [] }, '/vehicles': FORBIDDEN, '/alerts': { items: [], cursor: null, hasMore: false } });

    render(await DashboardPage());

    expect(screen.getByText(t('fleet.dashboard.wallet.pendingOrg'))).toBeTruthy();
    // The roster is approval-gated, so the denominator falls back to trackers and
    // the documents row is dropped rather than reported as zero.
    expect(screen.getByText(t('fleet.dashboard.kpi.ofTrackers', { count: 3 }))).toBeTruthy();
    expect(screen.queryByText(t('fleet.dashboard.alert.documentsOutstanding'))).toBeNull();
  });

  it('shows a problem panel when the health rollup — the screen itself — fails', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ ...FULL, '/health': FORBIDDEN });

    // Asserted on the returned tree rather than on the DOM: `ProblemPanel` is an
    // async server component and only Next can render one (`test/org-screens`
    // makes the same call, and `test/problem.test.ts` covers what it says). What
    // matters here is that the screen still resolves — the counters read zero and
    // the timestamp says the rollup could not be read, rather than the page dying.
    const tree = await DashboardPage();
    expect(tree).toBeTruthy();

    expect(JSON.stringify(tree)).toContain(t('fleet.dashboard.asOfUnknown'));
  });

  it('names no on-demand mode anywhere on the screen (AL-03)', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    const { container } = render(await DashboardPage());
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-007
 * ------------------------------------------------------------------------ */

describe('SCR-FP-007 · live fleet map', () => {
  const FULL = {
    '/map': { vehicles: POSITIONS, asOf: '2026-06-17T04:30:00Z' },
    '/health': HEALTHY,
    '/vehicles': ROSTER,
    '/assignments': ASSIGNMENTS,
  };

  it('never names an organisation — every read is org-relative', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    await MapPage({ searchParams: Promise.resolve({}) });

    for (const [options] of read.mock.calls) {
      expect(Object.keys(options as object)).toEqual(['org']);
      expect((options as { org: string }).org).not.toContain('fleets');
    }
  });

  it('hands the map only the vehicles this organisation’s own read answered for', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({}) }));

    const props = mapProps.mock.calls.at(-1)?.[0] as {
      vehicles: { vehicleId: string; live: boolean }[];
    };

    expect(props.vehicles.map((vehicle) => vehicle.vehicleId)).toEqual([BUS, VAN]);
    expect(props.vehicles.some((vehicle) => vehicle.vehicleId === OTHER_ORG_VEHICLE)).toBe(false);
  });

  it('resolves a vehicle id from outside the organisation to nothing at all', async () => {
    // The row-level-security fence is the database's (C059's `RowLevelSecurityTests`
    // proves it against a real non-superuser login). What *this* side has to hold
    // is that a pasted id from another org cannot become a marker or a panel of
    // facts — it resolves to the "not in this organisation" state.
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({ vehicle: OTHER_ORG_VEHICLE }) }));

    expect(screen.getByText(t('fleet.map.detail.unknown'))).toBeTruthy();

    const props = mapProps.mock.calls.at(-1)?.[0] as { selectedVehicleId: string | null };
    expect(props.selectedVehicleId).toBe(null);
  });

  it('follows Online → Stale → Offline in the overlay and dims all but Online', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));

    for (const [state, label] of [
      ['online', 'fleet.trackers.state.online'],
      ['stale', 'fleet.trackers.state.stale'],
      ['offline', 'fleet.trackers.state.offline'],
    ] as const) {
      cleanup();
      serve({ ...FULL, '/health': health([tracker(BUS, state)]) });

      render(await MapPage({ searchParams: Promise.resolve({}) }));

      const table = screen.getByRole('table');
      expect(within(table).getAllByText(t(label)).length).toBeGreaterThan(0);

      const props = mapProps.mock.calls.at(-1)?.[0] as {
        vehicles: { vehicleId: string; live: boolean }[];
      };
      const bus = props.vehicles.find((vehicle) => vehicle.vehicleId === BUS);
      expect(bus?.live, `${state} should be drawn ${state === 'online' ? 'solid' : 'dimmed'}`).toBe(
        state === 'online',
      );
    }
  });

  it('lists a vehicle the map has dropped, because the two windows differ', async () => {
    // COACH is offline (silent > 30 min) and is therefore not in the map answer
    // at all (silent > 15 min). A table built from the pins would lose it.
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({}) }));

    const table = screen.getByRole('table');
    expect(within(table).getByText('NC-1200')).toBeTruthy();

    const props = mapProps.mock.calls.at(-1)?.[0] as { vehicles: { vehicleId: string }[] };
    expect(props.vehicles.some((vehicle) => vehicle.vehicleId === COACH)).toBe(false);

    expect(screen.getByText(t('fleet.map.windows', { map: 15, stale: 5, offline: 30 }))).toBeTruthy();
  });

  it('draws the wireframe’s five overlay columns and its three counters', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({}) }));

    const headers = within(screen.getByRole('table'))
      .getAllByRole('columnheader')
      .map((header) => header.textContent);

    expect(headers).toEqual([
      t('fleet.map.column.vehicle'),
      t('fleet.map.column.driver'),
      t('fleet.map.column.speed'),
      t('fleet.map.column.battery'),
      t('fleet.map.column.health'),
    ]);

    expect(screen.getByText(t('fleet.map.count.online', { count: 1 }))).toBeTruthy();
    expect(screen.getByText(t('fleet.map.count.stale', { count: 1 }))).toBeTruthy();
    expect(screen.getByText(t('fleet.map.count.offline', { count: 1 }))).toBeTruthy();

    // The driver comes from the *active* assignment only.
    expect(screen.getByText('K. Fernando')).toBeTruthy();
    expect(screen.queryByText('S. Bandara')).toBeNull();

    // m/s on the wire, km/h on the screen; and battery in whichever unit arrived.
    expect(screen.getByText(t('fleet.map.speedKmh', { speed: 34 }))).toBeTruthy();
    expect(screen.getByText(t('fleet.map.batteryPct', { percent: 62 }))).toBeTruthy();
    expect(screen.getByText(t('fleet.map.batteryMv', { mv: 3900 }))).toBeTruthy();
  });

  it('drills into one vehicle from the query string', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({ vehicle: BUS }) }));

    const panel = screen.getByRole('region', { name: t('fleet.map.detail.heading') });

    expect(within(panel).getByText('NB-4521')).toBeTruthy();
    expect(
      within(panel).getByText(t('fleet.map.headingDegrees', { degrees: 214, compass: 'SW' })),
    ).toBeTruthy();

    const props = mapProps.mock.calls.at(-1)?.[0] as { selectedVehicleId: string | null };
    expect(props.selectedVehicleId).toBe(BUS);
  });

  it('opens for a pending organisation, with plates and no drivers', async () => {
    getSession.mockResolvedValue(sessionFor('viewer', 'PENDING'));
    serve({
      '/map': { vehicles: POSITIONS, asOf: '2026-06-17T04:30:00Z' },
      '/health': HEALTHY,
      '/vehicles': FORBIDDEN,
      '/assignments': FORBIDDEN,
    });

    render(await MapPage({ searchParams: Promise.resolve({}) }));

    const table = screen.getByRole('table');
    expect(within(table).getByText('NB-4521')).toBeTruthy();
    expect(within(table).getAllByText(t('fleet.map.noDriver')).length).toBeGreaterThan(0);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('says which map it is drawing when the deployment has no tiles', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await MapPage({ searchParams: Promise.resolve({}) }));

    expect(screen.getByText(t('fleet.map.noBasemap'))).toBeTruthy();
    expect(screen.getByText(t('fleet.map.scoping'))).toBeTruthy();
  });

  it('names no on-demand mode anywhere on the screen (AL-03)', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    const { container } = render(await MapPage({ searchParams: Promise.resolve({}) }));
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-009
 * ------------------------------------------------------------------------ */

describe('SCR-FP-009 · trip history & analytics', () => {
  const FULL = { '/analytics': ANALYTICS, '/vehicles': ROSTER };

  it('sends the period it names, rather than letting the service default it', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    await AnalyticsPage({
      searchParams: Promise.resolve({ from: '2026-06-01', to: '2026-06-02' }),
    });

    const analytics = read.mock.calls
      .map(([options]) => options as { org?: string; searchParams?: Record<string, string> })
      .find((options) => options.org === '/analytics');

    expect(analytics?.searchParams).toEqual({ from: '2026-06-01', to: '2026-06-02' });
  });

  it('reconciles the tiles with the rows under them', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(
      await AnalyticsPage({
        searchParams: Promise.resolve({ from: '2026-06-01', to: '2026-06-30' }),
      }),
    );

    // 318 + 96 trips; 4,120 + 2,010 km.
    expect(screen.getByText('414')).toBeTruthy();
    expect(screen.getByText(t('fleet.analytics.km', { distance: '6,130' }))).toBeTruthy();

    // Σ active (360 h) over Σ available (2 vehicles × 30 days × 24 h = 1,440 h).
    expect(screen.getByText(t('fleet.analytics.percent', { percent: '25' }))).toBeTruthy();

    // Idle: (720 − 240) + (720 − 120) = 1,080 h ÷ 2 vehicles ÷ 30 days = 18 h.
    expect(screen.getByText(t('fleet.analytics.hours', { hours: '18' }))).toBeTruthy();

    expect(
      screen.getByText(
        t('fleet.analytics.period', { from: 'Jun 1, 2026', to: 'Jun 30, 2026', days: 30 }),
      ),
    ).toBeTruthy();
  });

  it('draws the wireframe’s five per-vehicle columns and every vehicle in them', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await AnalyticsPage({ searchParams: Promise.resolve({}) }));

    const headers = within(screen.getByRole('table'))
      .getAllByRole('columnheader')
      .map((header) => header.textContent);

    expect(headers).toEqual([
      t('fleet.analytics.column.vehicle'),
      t('fleet.analytics.column.trips'),
      t('fleet.analytics.column.distance'),
      t('fleet.analytics.column.utilisation'),
      t('fleet.analytics.column.idle'),
    ]);

    expect(screen.getByText('NB-4521')).toBeTruthy();
    expect(screen.getByText('VN-8810')).toBeTruthy();
  });

  it('says what the report cannot report', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(await AnalyticsPage({ searchParams: Promise.resolve({}) }));

    expect(screen.getByText(t('fleet.analytics.earningsNote'))).toBeTruthy();
    expect(screen.getByText(t('fleet.analytics.distanceNote'))).toBeTruthy();
    expect(screen.getByText(t('fleet.analytics.idleNote'))).toBeTruthy();
  });

  it('says so when the period it was asked for could not be reported', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(
      await AnalyticsPage({
        searchParams: Promise.resolve({ from: '2020-01-01', to: '2026-06-17' }),
      }),
    );

    expect(screen.getByText(t('fleet.analytics.rangeAdjusted', { days: 92 }))).toBeTruthy();
  });

  it('hands the CSV export the same period the screen is showing', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    render(
      await AnalyticsPage({
        searchParams: Promise.resolve({ from: '2026-06-01', to: '2026-06-17' }),
      }),
    );

    const csv = screen.getByText(t('fleet.analytics.exportCsv'));
    expect(csv.getAttribute('href')).toBe('/analytics/export?from=2026-06-01&to=2026-06-17');
    expect(csv.hasAttribute('download')).toBe(true);
  });

  it('renders for a Viewer, and draws no mutating control', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    const { container } = render(await AnalyticsPage({ searchParams: Promise.resolve({}) }));

    // The only form on the screen is the `GET` range filter.
    for (const form of Array.from(container.querySelectorAll('form'))) {
      expect((form.getAttribute('method') ?? 'get').toLowerCase()).toBe('get');
    }
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });
});
