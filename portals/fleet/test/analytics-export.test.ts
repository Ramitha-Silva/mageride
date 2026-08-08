import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { VehicleAnalytics } from '@/api/insights';
import { ProblemError } from '@/api/problem';
import type { FleetVehicle } from '@/api/vehicles';
import { createFleetTranslator } from '@/i18n';
import { dispositionFor } from '@/server/access';
import { resolveScreenRoute } from '@/server/routes';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-009's CSV export**, which is a route handler in this application
 * rather than a call to a platform route — because there is no analytics export
 * route on any contract, and `exportFleetInvoice` is fleet-billing-svc's and
 * produces a different document.
 *
 * Two things have to be true of it and are easy to lose:
 *
 *  1. it is **gated by the same thing the screen is** — its path is claimed by
 *     SCR-FP-009, so `proxy.ts` refuses it for a caller whose seat does not carry
 *     that screen, before the handler runs;
 *  2. it reports **the period the screen was showing**, through the same
 *     org-scoped `read()`, so the file and the page cannot disagree.
 */

const getSession = vi.fn();
const read = vi.fn();

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/api/client', () => ({ read: (options: unknown) => read(options), mutate: vi.fn() }));
vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

const { GET } = await import('../app/(portal)/analytics/export/route');
const { NextRequest } = await import('next/server');

const BUS = '01JQV000000000000000000001';

const ANALYTICS: { items: VehicleAnalytics[] } = {
  items: [
    {
      vehicleId: BUS,
      registrationNumber: 'NB-4521',
      tripCount: 318,
      distanceKm: 4120.125,
      activeHours: 240,
      utilisationPct: 33.33,
    },
  ],
};

const ROSTER: { items: FleetVehicle[] } = {
  items: [
    {
      vehicleId: BUS,
      registrationNumber: 'NB-4521',
      vehicleType: 'bus',
      mode: 'A',
      status: 'APPROVED',
    },
  ],
};

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

function request(query: string) {
  return new NextRequest(`https://fleet.mageride.lk/analytics/export${query}`);
}

beforeEach(() => vi.clearAllMocks());

describe('the export path is the analytics screen’s', () => {
  it('resolves to SCR-FP-009, so the proxy gates it as that screen', () => {
    const screen = resolveScreenRoute('/analytics/export');

    expect(screen?.key).toBe('analytics');
    expect(screen?.screen).toBe('SCR-FP-009');
  });

  it('is refused for a caller whose seat does not carry the screen', () => {
    // The analytics screen is `fleet-monitoring: read`, which every sub-role
    // holds — so the case that matters is an account with no fleet seat at all.
    expect(dispositionFor(sessionFor('viewer'), '/analytics/export')).toBe('render');

    const outsider = { ...sessionFor('viewer'), permissions: [], fleetRole: null, fleetId: null };
    expect(dispositionFor(outsider, '/analytics/export')).toBe('denied');
  });
});

describe('the file', () => {
  it('reports the period the link carried, through the org-scoped read', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({ '/analytics': ANALYTICS, '/vehicles': ROSTER });

    const response = await GET(request('?from=2026-06-01&to=2026-06-02'));

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/csv; charset=utf-8');
    expect(response.headers.get('content-disposition')).toBe(
      'attachment; filename="mageride-fleet-analytics-2026-06-01-to-2026-06-02.csv"',
    );
    // One organisation's figures under a per-caller evaluation.
    expect(response.headers.get('cache-control')).toContain('no-store');

    const analytics = read.mock.calls
      .map(([options]) => options as { org?: string; searchParams?: Record<string, string> })
      .find((options) => options.org === '/analytics');

    expect(analytics?.searchParams).toEqual({ from: '2026-06-01', to: '2026-06-02' });
  });

  it('writes a BOM, a translated header and raw numbers a spreadsheet can add up', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({ '/analytics': ANALYTICS, '/vehicles': ROSTER });

    const response = await GET(request('?from=2026-06-01&to=2026-06-02'));

    // Asserted on the **bytes**, because a UTF-8 decode strips a leading BOM —
    // `response.text()` would report it absent from a file that has it. Excel is
    // what reads those three bytes, and without them it reads the file as
    // Windows-1252 and turns a Sinhala plate note into mojibake.
    const bytes = new Uint8Array(await response.clone().arrayBuffer());
    expect([bytes[0], bytes[1], bytes[2]]).toEqual([0xef, 0xbb, 0xbf]);

    const body = await response.text();
    const [header, row] = body.split('\r\n');

    expect(header).toBe(
      'Vehicle ID,Vehicle,Type,Mode,Trips,Distance (km),Active hours,Utilisation (%),Idle hours',
    );

    // Raw, ungrouped, `.`-separated — a grouped "4,120" would land in the next
    // column, and a localised numeral would not parse as a number at all.
    // Idle: 2 days × 24 h = 48 available, minus 240 active, floored at 0.
    expect(row).toBe(`${BUS},NB-4521,bus,A,318,4120.125,240,33.33,0`);
  });

  it('sends a signed-out caller to sign in rather than to a CSV of nothing', async () => {
    getSession.mockResolvedValue(null);

    const response = await GET(request('?from=2026-06-01&to=2026-06-02'));

    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toContain('/login');
  });

  it('sends a failed read back to the screen, where a panel can explain it', async () => {
    // A problem here would otherwise be an error page in a file the browser saved
    // as `.csv`.
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({
      '/analytics': new ProblemError({
        type: 'https://mageride.lk/errors/dependency-unavailable',
        title: 'x',
        status: 503,
      }),
    });

    const response = await GET(request('?from=2026-06-01&to=2026-06-02'));

    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toContain('/analytics?from=2026-06-01&to=2026-06-02');
  });
});
