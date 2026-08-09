import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ProblemError } from '@/api/problem';
import type { SubscriberRow, SubscriptionPaymentRow } from '@/api/subscriptions';
import type { FleetVehicle } from '@/api/vehicles';
import { createFleetTranslator } from '@/i18n';
import { dispositionFor } from '@/server/access';
import { resolveScreenRoute } from '@/server/routes';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-012's CSV export**, which is a route handler in this application
 * rather than a call to a platform route — because **no contract has a
 * subscription-payment export**: `fleet.yaml`'s Epic 23 block is eight proxies
 * and none renders a document, and subscription-svc's only document route is the
 * signed slip/QR file.
 *
 * Three things have to be true of it and are easy to lose:
 *
 *  1. it is **gated by the same thing the screen is** — its path is claimed by
 *     SCR-FP-012, so `proxy.ts` refuses it for a caller whose seat does not carry
 *     that screen, and the handler checks the Owner seat again itself;
 *  2. it reports **the ledger the screen was showing**, through the same
 *     org-scoped `read()`, so the file and the page cannot disagree;
 *  3. it prints the money **twice** — grouped rupees for a person, integer minor
 *     units for a reconciliation against this platform.
 */

const getSession = vi.fn();
const read = vi.fn();

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/api/client', () => ({ read: (options: unknown) => read(options), mutate: vi.fn() }));
vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

const { GET } = await import('../app/(portal)/payments/export/route');
const { NextRequest } = await import('next/server');

const VAN = '01JQV000000000000000000002';
const SUBSCRIBER = 's2';

const ROSTER: { items: FleetVehicle[] } = {
  items: [
    {
      vehicleId: VAN,
      registrationNumber: 'VN-8810',
      vehicleType: 'van',
      mode: 'B',
      status: 'APPROVED',
      modeBBilling: 'paid',
    },
  ],
};

const SUBSCRIBERS: { items: SubscriberRow[] } = {
  items: [
    {
      subscriberId: SUBSCRIBER,
      passengerId: 'p2',
      name: 'K. Silva',
      billing: 'paid',
      monthlyFareMinor: 550_000,
      currency: 'LKR',
      cycle: 'month_first',
      thisMonthStatus: 'paid',
      muted: false,
      status: 'active',
    },
  ],
};

const LEDGER: { items: SubscriptionPaymentRow[] } = {
  items: [
    {
      paymentId: 'pay-may',
      subscriptionId: 'sub2',
      method: 'cash',
      amountMinor: 550_000,
      currency: 'LKR',
      status: 'paid',
      periodMonth: '2026-05-01',
      paidAt: '2026-05-06T04:30:00Z',
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

const FULL = {
  '/vehicles': ROSTER,
  [`/vehicles/${VAN}/subscribers`]: SUBSCRIBERS,
  [`/vehicles/${VAN}/subscribers/${SUBSCRIBER}/payments`]: LEDGER,
};

function request(query: string) {
  return new NextRequest(`https://fleet.mageride.lk/payments/export${query}`);
}

const SCOPE = `?vehicle=${VAN}&subscriber=${SUBSCRIBER}`;

beforeEach(() => vi.clearAllMocks());

describe('the export path is the payments screen’s', () => {
  it('resolves to SCR-FP-012, so the proxy gates it as that screen', () => {
    const screen = resolveScreenRoute('/payments/export');

    expect(screen?.key).toBe('payments');
    expect(screen?.screen).toBe('SCR-FP-012');
  });

  it('is the Owner’s, exactly as the ledger route is', () => {
    expect(dispositionFor(sessionFor('owner'), '/payments/export')).toBe('render');
    expect(dispositionFor(sessionFor('manager'), '/payments/export')).toBe('denied');
    expect(dispositionFor(sessionFor('viewer'), '/payments/export')).toBe('denied');
    // The whole proxy group is inside `RequireApprovedFleet()`.
    expect(dispositionFor(sessionFor('owner', 'PENDING'), '/payments/export')).toBe('pending');
  });
});

describe('the file', () => {
  it('reports the ledger the link named, through the org-scoped read', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    const response = await GET(request(SCOPE));

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/csv; charset=utf-8');
    expect(response.headers.get('content-disposition')).toBe(
      `attachment; filename="mageride-subscriber-payments-vn-8810-${SUBSCRIBER}.csv"`,
    );
    // One organisation's money under a per-caller evaluation.
    expect(response.headers.get('cache-control')).toContain('no-store');
  });

  it('writes a BOM, a translated header and the amount in both units', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    const response = await GET(request(SCOPE));

    // Asserted on the **bytes**: a UTF-8 decode strips a leading BOM, and Excel
    // is what reads those three — without them a Sinhala subscriber name comes
    // out as mojibake in the one program every operator has.
    const bytes = new Uint8Array(await response.clone().arrayBuffer());
    expect([bytes[0], bytes[1], bytes[2]]).toEqual([0xef, 0xbb, 0xbf]);

    const [header, row] = (await response.text()).split('\r\n');

    expect(header).toBe(
      'Vehicle,Subscriber,Period month,Paid at,Method,Amount (Rs),Amount (cents),Currency,Status,Payment ID',
    );
    expect(row).toBe(
      'VN-8810,K. Silva,2026-05-01,2026-05-06T04:30:00Z,Cash,"5,500",550000,LKR,Paid,pay-may',
    );
  });

  it('refuses a Manager here as well as at the proxy', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    const response = await GET(request(SCOPE));

    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toContain('/payments');
    // A route handler that assumed a guard upstream stops being guarded the day
    // the guard moves, so nothing was read.
    expect(read).not.toHaveBeenCalled();
  });

  it('sends a signed-out caller to sign in rather than to a CSV of nothing', async () => {
    getSession.mockResolvedValue(null);

    const response = await GET(request(SCOPE));

    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toContain('/login');
  });

  it('sends a failed read back to the screen, where a panel can explain it', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({
      ...FULL,
      [`/vehicles/${VAN}/subscribers/${SUBSCRIBER}/payments`]: new ProblemError({
        type: 'https://mageride.lk/errors/dependency-unavailable',
        title: 'x',
        status: 503,
      }),
    });

    const response = await GET(request(SCOPE));

    expect(response.status).toBe(307);
    expect(response.headers.get('location')).toContain(`/payments?vehicle=${VAN}`);
  });
});
