import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ProblemError } from '@/api/problem';
import type { AccessRequest, SubscriberRow, SubscriptionPaymentRow } from '@/api/subscriptions';
import type { FleetVehicle } from '@/api/vehicles';
import { createFleetTranslator } from '@/i18n';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-011 and SCR-FP-012 on the rendered page** — this component's
 * Definition of Done, where an operator would meet it.
 *
 * Four of the five items cannot be checked anywhere else:
 *
 *  1. **accepting a request creates the grant and starts the subscription** —
 *     asserted as the call the Accept button makes, on the vehicle's own route;
 *  2. **confirming a transfer slip moves the payment to Paid and updates the
 *     KPIs** — the confirm goes to the payment-addressed route, and the tiles are
 *     the roster's own answer about this month, so a confirmed month moves from
 *     "pending verify" to "paid" on the next render;
 *  3. **a muted unsubscribed row persists until the owner deletes it** (AL-25) —
 *     the row is on the screen, greyed, with Delete and with no month verbs;
 *  4. **the money verbs are the Owner's** — a Manager gets the queue and the
 *     roster and neither the fares nor the ledger, and SCR-FP-012 makes **no
 *     request at all** for them.
 *
 * The fifth — every screen matching its wireframe — is the column assertions.
 */

const redirected = new Error('NEXT_REDIRECT');

const redirect = vi.fn(() => {
  throw redirected;
});
const getSession = vi.fn();
const read = vi.fn();
const mutate = vi.fn();
const revalidatePath = vi.fn();

vi.mock('next/navigation', () => ({
  redirect: () => redirect(),
  permanentRedirect: () => redirect(),
  forbidden: () => {
    throw new Error('NEXT_FORBIDDEN');
  },
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  usePathname: () => '/subscriptions',
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
vi.mock('@/api/client', () => ({
  read: (options: unknown) => read(options),
  mutate: (options: unknown) => mutate(options),
  download: vi.fn(),
}));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));

const { default: SubscriptionsPage } = await import('../app/(portal)/subscriptions/page');
const { default: PaymentsPage } = await import('../app/(portal)/payments/page');
const {
  acceptRequest,
  confirmTransfer,
  deleteSubscriber,
  markCashReceived,
  setSubscriberFare,
} = await import('@/server/subscription-actions');

const t = createFleetTranslator('en');

/* ---------------------------------------------------------------------------
 * Fixtures — the wireframe's own vehicle, queue and roster
 * ------------------------------------------------------------------------ */

const BUS = '01JQV000000000000000000001';
const VAN = '01JQV000000000000000000002';
const OFFICE = '01JQV000000000000000000003';

const ROSTER: { items: FleetVehicle[] } = {
  items: [
    // Mode A: no subscribers by construction, so it is never offered.
    { vehicleId: BUS, registrationNumber: 'NB-4521', vehicleType: 'bus', mode: 'A', status: 'APPROVED' },
    {
      vehicleId: VAN,
      registrationNumber: 'VN-8810',
      vehicleType: 'van',
      mode: 'B',
      status: 'APPROVED',
      modeBBilling: 'paid',
      defaultMonthlyFareMinor: 600_000,
    },
    {
      vehicleId: OFFICE,
      registrationNumber: 'VN-9911',
      vehicleType: 'van',
      mode: 'B',
      status: 'APPROVED',
      modeBBilling: 'free',
    },
  ],
};

const REQUESTS: { items: AccessRequest[]; cursor: null; hasMore: boolean } = {
  items: [
    {
      requestId: 'r1',
      vehicleId: VAN,
      passengerId: '01JQX000000000000000000001',
      passengerName: 'Sunethra',
      passengerMobileMasked: '+94 77 *** 0345',
      status: 'pending',
      createdAt: '2026-06-20T04:30:00Z',
    },
    {
      requestId: 'r2',
      vehicleId: VAN,
      passengerId: '01JQX000000000000000000002',
      passengerName: 'N. Jayasuriya',
      passengerMobileMasked: '+94 71 *** 9090',
      status: 'pending',
      createdAt: '2026-06-21T04:30:00Z',
    },
    // Already decided — the queue is the pending ones, so this is not drawn.
    {
      requestId: 'r3',
      vehicleId: VAN,
      passengerId: '01JQX000000000000000000003',
      passengerName: 'Decided',
      status: 'rejected',
      createdAt: '2026-06-19T04:30:00Z',
    },
  ],
  cursor: null,
  hasMore: false,
};

/** The sketch's four rows: paid, awaiting a slip, cash due, and one muted. */
const SUBSCRIBERS: { items: SubscriberRow[]; cursor: null; hasMore: boolean } = {
  items: [
    {
      subscriberId: 's1',
      passengerId: 'p1',
      name: 'Ramith de Silva',
      mobileMasked: '+94 77 *** 4567',
      billing: 'paid',
      monthlyFareMinor: 600_000,
      currency: 'LKR',
      cycle: 'join_anniversary',
      thisMonthStatus: 'paid',
      muted: false,
      status: 'active',
    },
    {
      subscriberId: 's2',
      passengerId: 'p2',
      name: 'K. Silva',
      mobileMasked: '+94 76 *** 1188',
      billing: 'paid',
      monthlyFareMinor: 550_000,
      currency: 'LKR',
      cycle: 'month_first',
      thisMonthStatus: 'pending_verification',
      muted: false,
      status: 'active',
    },
    {
      subscriberId: 's3',
      passengerId: 'p3',
      name: 'M. Perera',
      mobileMasked: '+94 77 *** 0011',
      billing: 'paid',
      monthlyFareMinor: 600_000,
      currency: 'LKR',
      cycle: 'join_anniversary',
      thisMonthStatus: 'unpaid',
      muted: false,
      status: 'active',
    },
    {
      subscriberId: 's4',
      passengerId: 'p4',
      name: 'T. Wijesinghe',
      mobileMasked: '+94 71 *** 2929',
      billing: 'paid',
      monthlyFareMinor: 600_000,
      currency: 'LKR',
      cycle: 'join_anniversary',
      thisMonthStatus: 'unpaid',
      muted: true,
      status: 'unsubscribed',
    },
  ],
  cursor: null,
  hasMore: false,
};

const S2_LEDGER: { items: SubscriptionPaymentRow[]; cursor: null; hasMore: boolean } = {
  items: [
    {
      paymentId: 'pay-jun',
      subscriptionId: 'sub2',
      method: 'online_transfer',
      amountMinor: 550_000,
      currency: 'LKR',
      status: 'pending_verification',
      periodMonth: '2026-06-01',
      slipUrl: 'https://api.mageride.lk/v1/mode-b/files/slips/pay-jun?expires=1&signature=x',
    },
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
    {
      paymentId: 'pay-apr',
      subscriptionId: 'sub2',
      method: 'lankaqr_deeplink',
      amountMinor: 550_000,
      currency: 'LKR',
      status: 'paid',
      periodMonth: '2026-04-01',
      paidAt: '2026-04-06T04:30:00Z',
    },
  ],
  cursor: null,
  hasMore: false,
};

const EMPTY_LEDGER = { items: [], cursor: null, hasMore: false };

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

const VAN_SCREEN = {
  '/vehicles': ROSTER,
  [`/vehicles/${VAN}/requests`]: REQUESTS,
  [`/vehicles/${VAN}/subscribers`]: SUBSCRIBERS,
  [`/vehicles/${VAN}/subscribers/s1/payments`]: EMPTY_LEDGER,
  [`/vehicles/${VAN}/subscribers/s2/payments`]: S2_LEDGER,
  [`/vehicles/${VAN}/subscribers/s3/payments`]: EMPTY_LEDGER,
  [`/vehicles/${VAN}/subscribers/s4/payments`]: EMPTY_LEDGER,
};

function query(values: Record<string, string> = {}) {
  return Promise.resolve(values as Record<string, string | string[] | undefined>);
}

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(values)) data.set(key, value);
  return data;
}

/** Every org-relative target `read()` was asked for, in order. */
function orgTargets(): string[] {
  return read.mock.calls.map((call) => String((call[0] as { org?: string } | undefined)?.org ?? ''));
}

/** One `KpiTiles` card, found by the label it is headed with. */
function tile(label: string): HTMLElement {
  return screen.getByText(label).parentElement!;
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200, idempotencyKey: 'k' });
});
afterEach(cleanup);

/* ---------------------------------------------------------------------------
 * SCR-FP-011
 * ------------------------------------------------------------------------ */

describe('SCR-FP-011 · Mode B subscriptions & requests', () => {
  it('draws the wireframe’s two tables with their own columns', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    const tables = screen.getAllByRole('table');
    expect(tables).toHaveLength(2);

    expect(
      within(tables[0]!)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual([
      t('fleet.subscriptions.column.passenger'),
      t('fleet.subscriptions.column.contact'),
      t('fleet.subscriptions.column.requested'),
      t('fleet.subscriptions.column.action'),
    ]);

    expect(
      within(tables[1]!)
        .getAllByRole('columnheader')
        .map((header) => header.textContent),
    ).toEqual([
      t('fleet.subscriptions.column.passenger'),
      t('fleet.subscriptions.column.fare'),
      t('fleet.subscriptions.column.cycle'),
      t('fleet.subscriptions.column.thisMonth'),
      t('fleet.subscriptions.column.actions'),
    ]);
  });

  it('is scoped to one vehicle, and offers only the Mode B ones (AL-23)', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    const picker = screen.getByLabelText(t('fleet.subscriptions.scope.vehicle'));
    const plates = within(picker).getAllByRole('option').map((option) => option.textContent);

    expect(plates).toHaveLength(2);
    expect(plates.some((plate) => plate?.includes('VN-8810'))).toBe(true);
    expect(plates.some((plate) => plate?.includes('NB-4521'))).toBe(false);

    // Both reads name the chosen vehicle. Nothing on this screen reads a
    // fleet-wide queue or roster, because neither exists.
    expect(orgTargets()).toContain(`/vehicles/${VAN}/requests`);
    expect(orgTargets()).toContain(`/vehicles/${VAN}/subscribers`);
  });

  it('shows the pending requests only, with the masked number the platform sent', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(screen.getByText('Sunethra')).toBeTruthy();
    expect(screen.getByText('N. Jayasuriya')).toBeTruthy();
    expect(screen.queryByText('Decided')).toBeNull();

    expect(screen.getByText('+94 77 *** 0345')).toBeTruthy();
    expect(screen.getByText(t('fleet.subscriptions.requests.pending', { count: 2 }))).toBeTruthy();
  });

  it('draws the sketch’s four month states, and the muted row survives (AL-25)', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(screen.getByText(t('fleet.subscriptions.status.paid'))).toBeTruthy();
    expect(screen.getByText(t('fleet.subscriptions.status.verify'))).toBeTruthy();
    expect(screen.getByText(t('fleet.subscriptions.status.due'))).toBeTruthy();

    // The unsubscribed passenger is still on the list, with Delete and with no
    // month verb — and nothing on this screen removed them.
    const muted = screen.getByText('T. Wijesinghe').closest('tr')!;
    expect(muted.className).toContain('opacity-60');
    expect(within(muted).getByText(t('fleet.subscriptions.status.unsubscribed'))).toBeTruthy();
    expect(within(muted).getByRole('button', { name: t('fleet.subscriptions.delete.open') })).toBeTruthy();
    expect(within(muted).queryByRole('button', { name: t('fleet.subscriptions.cash.open') })).toBeNull();
    expect(within(muted).queryByRole('button', { name: t('fleet.subscriptions.fare.edit') })).toBeNull();
  });

  it('offers Mark received where a month is owed and Confirm where a slip is waiting', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    // M. Perera owes this month: cash is the owner's to record.
    const owing = screen.getByText('M. Perera').closest('tr')!;
    expect(within(owing).getByRole('button', { name: t('fleet.subscriptions.cash.open') })).toBeTruthy();

    // K. Silva's transfer is waiting on the owner. The roster carries no payment
    // id, so the id came from her own ledger — one read, for the pending row only.
    const waiting = screen.getByText('K. Silva').closest('tr')!;
    expect(
      within(waiting).getByRole('button', { name: t('fleet.subscriptions.slip.confirm') }),
    ).toBeTruthy();
    expect(orgTargets()).toContain(
      `/vehicles/${VAN}/subscribers/s2/payments`,
    );

    // And the slip itself comes with it, so the owner looks before confirming.
    expect(
      within(waiting)
        .getByRole('link', { name: t('fleet.subscriptions.slip.view') })
        .getAttribute('href'),
    ).toContain('/v1/mode-b/files/slips/pay-jun');

    // Ramith has paid: neither verb is drawn for him.
    const paid = screen.getByText('Ramith de Silva').closest('tr')!;
    expect(within(paid).queryByRole('button', { name: t('fleet.subscriptions.cash.open') })).toBeNull();
  });

  it('heads the roster with the vehicle’s default fare, which every row may differ from', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(
      screen.getByText(t('fleet.subscriptions.roster.paidWithDefault', { amount: '6,000' })),
    ).toBeTruthy();

    // US-23.7: "each subscriber may pay a different amount" — K. Silva is on
    // Rs 5,500 under a Rs 6,000 default, and the column shows hers.
    expect(screen.getByText(t('fleet.subscriptions.farePerMonth', { amount: '5,500' }))).toBeTruthy();
  });

  it('says where the next-due date is, because the roster does not carry one', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(screen.getByText(t('fleet.subscriptions.cycleNote'))).toBeTruthy();
    expect(screen.getAllByText(t('fleet.subscriptions.cycle.anniversary')).length).toBeGreaterThan(0);
    expect(screen.getByText(t('fleet.subscriptions.cycle.monthFirst'))).toBeTruthy();
  });

  it('never presents this money as MageRide’s', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(screen.getByText(t('fleet.subscriptions.passThroughNote'))).toBeTruthy();
  });

  it('gives a Manager the queue and none of the money verbs', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(VAN_SCREEN);

    render(await SubscriptionsPage({ searchParams: query({ vehicle: VAN }) }));

    // US-23.1 gives Owner *and* Manager the same accept/reject.
    expect(screen.getAllByRole('button', { name: t('fleet.subscriptions.accept') })).toHaveLength(2);

    // Everything below `DELETE …/subscribers/{id}` is `RequireFleetSubRole(Owner)`.
    expect(screen.queryByRole('button', { name: t('fleet.subscriptions.fare.edit') })).toBeNull();
    expect(screen.queryByRole('button', { name: t('fleet.subscriptions.cash.open') })).toBeNull();
    expect(screen.queryByRole('button', { name: t('fleet.subscriptions.delete.open') })).toBeNull();
    expect(screen.getAllByText(t('fleet.subscriptions.ownerOnly')).length).toBeGreaterThan(0);

    // And the Owner-only ledger is not read to find a slip's payment id either.
    expect(orgTargets()).not.toContain(
      `/vehicles/${VAN}/subscribers/s2/payments`,
    );
  });

  it('says a free vehicle collects nothing rather than drawing empty fare columns', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({
      '/vehicles': ROSTER,
      [`/vehicles/${OFFICE}/requests`]: { items: [], cursor: null, hasMore: false },
      [`/vehicles/${OFFICE}/subscribers`]: { items: [], cursor: null, hasMore: false },
    });

    render(await SubscriptionsPage({ searchParams: query({ vehicle: OFFICE }) }));

    expect(screen.getByText(t('fleet.subscriptions.roster.free'))).toBeTruthy();
    expect(screen.getByText(t('fleet.subscriptions.freeVehicleNote'))).toBeTruthy();
  });

  it('answers a vehicle from another organisation with a sentence, not a request', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(
      await SubscriptionsPage({
        searchParams: query({ vehicle: '01JQV0000000000000000000ZZ' }),
      }),
    );

    expect(screen.getByText(t('fleet.subscriptions.unknownVehicle'))).toBeTruthy();
    expect(orgTargets()).toEqual(['/vehicles']);
  });
});

/* ---------------------------------------------------------------------------
 * The writes
 * ------------------------------------------------------------------------ */

describe('the six writes go where fleet.yaml puts them', () => {
  it('accepts a request on the vehicle’s own route, creating the grant', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));

    const state = await acceptRequest({}, form({ vehicleId: VAN, requestId: 'r1', passenger: 'Sunethra' }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        org: `/vehicles/${VAN}/requests/r1/accept`,
        requires: { area: 'fleet-operations', requiresApprovedOrg: true },
      }),
    );
    expect(state.done).toBe(t('fleet.subscriptions.request.accepted', { passenger: 'Sunethra' }));
    expect(revalidatePath).toHaveBeenCalledWith('/subscriptions');
  });

  it('sets a fare in integer minor units, from rupees as typed', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    await setSubscriberFare({}, form({ vehicleId: VAN, subscriberId: 's1', fare: '6,000' }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'PUT',
        org: `/vehicles/${VAN}/subscribers/s1/fare`,
        body: { monthlyFareMinor: 600_000 },
      }),
    );
  });

  it('marks cash without naming a month — the service knows which one is due', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    mutate.mockResolvedValue({
      data: { ...S2_LEDGER.items[1], amountMinor: 600_000, periodMonth: '2026-06-01' },
      status: 200,
      idempotencyKey: 'k',
    });

    const state = await markCashReceived(
      {},
      form({ vehicleId: VAN, subscriberId: 's3', amount: '6000' }),
    );

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        org: `/vehicles/${VAN}/subscribers/s3/mark-cash`,
        body: { amountMinor: 600_000 },
      }),
    );
    expect(state.done).toContain('June 2026');
  });

  it('confirms a slip by payment id, and says the passenger’s app now shows Paid', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    mutate.mockResolvedValue({
      data: { ...S2_LEDGER.items[0], status: 'paid', paidAt: '2026-06-22T04:30:00Z' },
      status: 200,
      idempotencyKey: 'k',
    });

    const state = await confirmTransfer({}, form({ paymentId: 'pay-jun' }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({ method: 'POST', org: '/payments/pay-jun/confirm' }),
    );
    expect(state.done).toBe(
      t('fleet.subscriptions.slip.confirmed', { amount: 'Rs 5,500', month: 'June 2026' }),
    );
    // Both screens are re-read, because both show this month's standing.
    expect(revalidatePath).toHaveBeenCalledWith('/subscriptions');
    expect(revalidatePath).toHaveBeenCalledWith('/payments');
  });

  it('refuses the Owner-only writes for a Manager before they leave the process', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));

    for (const state of [
      await setSubscriberFare({}, form({ vehicleId: VAN, subscriberId: 's1', fare: '6000' })),
      await markCashReceived({}, form({ vehicleId: VAN, subscriberId: 's3', amount: '6000' })),
      await confirmTransfer({}, form({ paymentId: 'pay-jun' })),
      await deleteSubscriber({}, form({ vehicleId: VAN, subscriberId: 's4' })),
    ]) {
      expect(state.message).toBe(t('fleet.subscriptions.ownerOnly'));
    }

    expect(mutate).not.toHaveBeenCalled();
  });

  it('deletes a muted subscriber, and says what the platform says about an active one', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    await deleteSubscriber({}, form({ vehicleId: VAN, subscriberId: 's4', passenger: 'T. Wijesinghe' }));
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({ method: 'DELETE', org: `/vehicles/${VAN}/subscribers/s4` }),
    );

    mutate.mockRejectedValueOnce(
      new ProblemError({ type: 'https://mageride.lk/errors/conflict', title: 'x', status: 409 }),
    );
    const refused = await deleteSubscriber({}, form({ vehicleId: VAN, subscriberId: 's1' }));
    expect(refused.message).toBe(t('fleet.subscriptions.error.stillSubscribed'));
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-012
 * ------------------------------------------------------------------------ */

describe('SCR-FP-012 · per-subscriber payment ledger', () => {
  it('draws the wireframe’s four KPIs over the roster’s own answer', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await PaymentsPage({ searchParams: query({ vehicle: VAN, subscriber: 's2' }) }));

    // Ramith paid Rs 6,000; K. Silva's Rs 5,500 is awaiting a slip check;
    // M. Perera's Rs 6,000 is still owed; three active and one unsubscribed.
    expect(within(tile(t('fleet.payments.kpi.collected'))).getByText('Rs 6,000')).toBeTruthy();
    expect(within(tile(t('fleet.payments.kpi.pending'))).getByText('Rs 5,500')).toBeTruthy();
    expect(within(tile(t('fleet.payments.kpi.due'))).getByText('Rs 6,000')).toBeTruthy();
    expect(within(tile(t('fleet.payments.kpi.subscribers'))).getByText('3')).toBeTruthy();

    expect(
      screen.getByText(t('fleet.payments.kpi.collectedDetail', { count: 1, vehicle: 'VN-8810' })),
    ).toBeTruthy();
    expect(screen.getByText(t('fleet.payments.kpi.dueDetail', { count: 1 }))).toBeTruthy();
    expect(
      screen.getByText(t('fleet.payments.kpi.subscribersDetail', { muted: 1, free: 0 })),
    ).toBeTruthy();

    // And the caption that says what the tiles are and are not.
    expect(screen.getByText(t('fleet.payments.kpiNote'))).toBeTruthy();
  });

  it('moves a month out of "pending verify" once the slip is confirmed', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    // The same screen after `confirmTransfer` succeeded: subscription-svc has
    // moved K. Silva's June to `paid`, so the roster answers differently and the
    // tiles follow it — Rs 11,500 in, nothing awaiting a check.
    const settled = {
      ...SUBSCRIBERS,
      items: SUBSCRIBERS.items.map((row) =>
        row.subscriberId === 's2' ? { ...row, thisMonthStatus: 'paid' as const } : row,
      ),
    };
    serve({ ...VAN_SCREEN, [`/vehicles/${VAN}/subscribers`]: settled });

    render(await PaymentsPage({ searchParams: query({ vehicle: VAN, subscriber: 's2' }) }));

    expect(screen.getByText('Rs 11,500')).toBeTruthy();
    expect(screen.getByText(t('fleet.payments.kpi.pendingDetail', { count: 0 }))).toBeTruthy();
  });

  it('lists every rail the platform records, and offers none', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    const { container } = render(
      await PaymentsPage({ searchParams: query({ vehicle: VAN, subscriber: 's2' }) }),
    );

    expect(screen.getByText(t('fleet.payments.method.onlineTransfer'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payments.method.cash'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payments.method.lankaqrDeeplink'))).toBeTruthy();

    // A payment is initiated by the passenger, never here (AL-59 also removed the
    // one rail that would have paid MageRide instead of the owner).
    expect(container.querySelector('select[name="method"]')).toBeNull();

    // The waiting slip is the owner's to confirm, and the screenshot is a link to
    // subscription-svc's own signed URL.
    const slip = screen.getByRole('link', { name: t('fleet.subscriptions.slip.view') });
    expect(slip.getAttribute('href')).toContain('/v1/mode-b/files/slips/pay-jun');
  });

  it('offers the CSV the platform has no route for, scoped to what is on screen', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await PaymentsPage({ searchParams: query({ vehicle: VAN, subscriber: 's2' }) }));

    const link = screen.getByRole('link', { name: t('fleet.payments.exportCsv') });
    expect(link.getAttribute('href')).toBe(`/payments/export?vehicle=${VAN}&subscriber=s2`);
  });

  it('is the Owner’s, and a Manager’s visit makes no request at all', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(VAN_SCREEN);

    render(await PaymentsPage({ searchParams: query({ vehicle: VAN }) }));

    expect(screen.getByText(t('fleet.payments.ownerOnly'))).toBeTruthy();
    expect(read).not.toHaveBeenCalled();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('keeps a muted subscriber’s ledger reachable, labelled as ended', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(VAN_SCREEN);

    render(await PaymentsPage({ searchParams: query({ vehicle: VAN, subscriber: 's4' }) }));

    const picker = screen.getByLabelText(t('fleet.subscriptions.scope.subscriber'));
    expect(
      within(picker).getByText(
        t('fleet.payments.scope.mutedOption', { passenger: 'T. Wijesinghe' }),
      ),
    ).toBeTruthy();
  });
});
