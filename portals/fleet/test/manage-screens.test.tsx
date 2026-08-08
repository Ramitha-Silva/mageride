import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { FleetInvoice, FleetInvoiceDetail, FleetWallet } from '@/api/billing';
import type { Assignment } from '@/api/drivers';
import { ProblemError } from '@/api/problem';
import type { FleetSchedule } from '@/api/schedules';
import type { FleetVehicle } from '@/api/vehicles';
import { createFleetTranslator } from '@/i18n';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-008 and SCR-FP-010 on the rendered page** — this component's
 * Definition of Done, where an operator would meet it.
 *
 * Four items cannot be checked anywhere else:
 *
 *  1. an invoice's per-vehicle lines **sum to its total and exclude Mode A
 *     vehicles**, as a number on the screen rather than as a property of a pure
 *     function;
 *  2. a configured alarm is what fires **in the assigned driver's app** — the
 *     screen names the recipient before the departure and reports the alarm after;
 *  3. billing is the Owner's, on reads, and a Manager's visit makes **no request
 *     at all**;
 *  4. neither screen draws a control the platform has no route for — no alarm
 *     toggle, no cancel, no bank transfer, and nothing on-demand (AL-03).
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
  usePathname: () => '/billing',
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

const { default: SchedulingPage } = await import('../app/(portal)/scheduling/page');
const { default: BillingPage } = await import('../app/(portal)/billing/page');
const { createSchedule } = await import('@/server/schedule-actions');
const { topUpWallet } = await import('@/server/billing-actions');

const t = createFleetTranslator('en');

/* ---------------------------------------------------------------------------
 * Fixtures — one organisation's fleet, one month's invoice
 * ------------------------------------------------------------------------ */

const BUS = '01JQV000000000000000000001';
const VAN = '01JQV000000000000000000002';
const COACH = '01JQV000000000000000000003';

const ROSTER: { items: FleetVehicle[] } = {
  items: [
    { vehicleId: BUS, registrationNumber: 'NB-4521', vehicleType: 'bus', mode: 'A', status: 'APPROVED' },
    { vehicleId: VAN, registrationNumber: 'VN-8810', vehicleType: 'van', mode: 'B', status: 'APPROVED' },
    { vehicleId: COACH, registrationNumber: 'NC-1200', vehicleType: 'bus', mode: 'A', status: 'PENDING' },
  ],
};

const ASSIGNMENTS: { items: Assignment[] } = {
  items: [
    {
      assignmentId: 'a1',
      driverId: 'd1',
      vehicleId: BUS,
      driverName: 'K. Fernando',
      from: '2026-06-01T00:00:00Z',
      active: true,
    },
    // Ends before the van's departure — the shift that changed in between.
    {
      assignmentId: 'a2',
      driverId: 'd2',
      vehicleId: VAN,
      driverName: 'S. Bandara',
      from: '2026-06-01T00:00:00Z',
      to: '2026-06-10T00:00:00Z',
      active: false,
    },
  ],
};

const SCHEDULES: { items: FleetSchedule[] } = {
  items: [
    // The sketch's three rows: one made, one missed with the alarm rung, one still to come.
    {
      scheduleId: 's1',
      vehicleId: BUS,
      departAt: '2026-06-18T00:30:00Z',
      notStartedAlarmMinutes: 10,
      status: 'STARTED',
    },
    {
      scheduleId: 's2',
      vehicleId: BUS,
      departAt: '2026-06-18T01:00:00Z',
      notStartedAlarmMinutes: 10,
      status: 'MISSED',
      alarmRaisedAt: '2026-06-18T01:10:00Z',
    },
    {
      scheduleId: 's3',
      vehicleId: VAN,
      departAt: '2026-06-18T01:30:00Z',
      notStartedAlarmMinutes: 15,
      status: 'SCHEDULED',
    },
  ],
};

const WALLET: FleetWallet = {
  balanceMinor: 8_450_000,
  outstandingMinor: 6_900_000,
  availableMinor: 1_550_000,
  currency: 'LKR',
  updatedAt: '2026-06-18T04:30:00Z',
  movements: [
    {
      entryId: 'e1',
      kind: 'topup',
      amountMinor: 5_000_000,
      balanceAfterMinor: 8_450_000,
      ts: '2026-06-17T04:30:00Z',
    },
    {
      entryId: 'e2',
      kind: 'fleet_invoice',
      amountMinor: -6_600_000,
      balanceAfterMinor: 3_450_000,
      ts: '2026-05-08T04:30:00Z',
    },
  ],
};

const JUNE: FleetInvoice = {
  invoiceId: 'i1',
  periodMonth: '2026-06-01',
  amountMinor: 6_900_000,
  currency: 'LKR',
  status: 'DUE',
  vehicleCount: 230,
  dueAt: '2026-06-15T00:00:00Z',
};

const MAY: FleetInvoice = {
  invoiceId: 'i2',
  periodMonth: '2026-05-01',
  amountMinor: 6_600_000,
  currency: 'LKR',
  status: 'PAID',
  vehicleCount: 220,
  settledAt: '2026-05-08T04:30:00Z',
  journalEntryId: 'j1',
};

const INVOICES = { items: [JUNE, MAY], cursor: null, hasMore: false };

/** Two charged Mode B vehicles and one in its free first month. Σ = Rs 69,000. */
const JUNE_DETAIL: FleetInvoiceDetail = {
  invoice: JUNE,
  lines: [
    {
      vehicleId: VAN,
      registrationNumber: 'VN-8810',
      vehicleType: 'van',
      amountMinor: 3_450_000,
      currency: 'LKR',
      status: 'DUE',
    },
    {
      vehicleId: '01JQV000000000000000000004',
      registrationNumber: 'VN-9911',
      vehicleType: 'van',
      amountMinor: 3_450_000,
      currency: 'LKR',
      status: 'DUE',
    },
    {
      vehicleId: '01JQV000000000000000000005',
      registrationNumber: 'VN-1234',
      vehicleType: 'van',
      amountMinor: 0,
      currency: 'LKR',
      status: 'FREE',
    },
  ],
  lineSumMinor: 6_900_000,
};

const MAY_DETAIL: FleetInvoiceDetail = {
  invoice: MAY,
  lines: [
    {
      vehicleId: VAN,
      registrationNumber: 'VN-8810',
      vehicleType: 'van',
      amountMinor: 6_600_000,
      currency: 'LKR',
      status: 'DUE',
    },
  ],
  lineSumMinor: 6_600_000,
};

const RECEIPT = {
  invoiceId: 'i2',
  fleetId: '01JQF0000000000000000000FL',
  fleetName: 'Lanka Transit (Pvt) Ltd',
  periodMonth: '2026-05-01',
  amountMinor: 6_600_000,
  currency: 'LKR',
  settledAt: '2026-05-08T04:30:00Z',
  journalEntryId: 'j1',
};

const FORBIDDEN = new ProblemError({
  type: 'https://mageride.lk/errors/fleet-not-approved',
  title: 'x',
  status: 403,
});

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

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(values)) data.set(key, value);
  return data;
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200, idempotencyKey: 'k' });
});
afterEach(cleanup);

/* ---------------------------------------------------------------------------
 * SCR-FP-008
 * ------------------------------------------------------------------------ */

describe('SCR-FP-008 · scheduling & alarms', () => {
  const FULL = { '/schedules': SCHEDULES, '/vehicles': ROSTER, '/assignments': ASSIGNMENTS };

  it('draws the wireframe’s five columns', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    render(await SchedulingPage());

    const headers = within(screen.getAllByRole('table')[0]!)
      .getAllByRole('columnheader')
      .map((header) => header.textContent);

    expect(headers).toEqual([
      t('fleet.scheduling.column.vehicle'),
      t('fleet.scheduling.column.route'),
      t('fleet.scheduling.column.start'),
      t('fleet.scheduling.column.alarm'),
      t('fleet.scheduling.column.status'),
    ]);
  });

  it('draws the sketch’s three states, and the alarm as an offset rather than a switch', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    const { container } = render(await SchedulingPage());

    expect(screen.getByText(t('fleet.scheduling.status.started'))).toBeTruthy();
    expect(screen.getByText(t('fleet.scheduling.status.missed'))).toBeTruthy();
    expect(screen.getByText(t('fleet.scheduling.status.scheduled'))).toBeTruthy();

    expect(screen.getAllByText(t('fleet.scheduling.alarmOffset', { minutes: 10 }))).toHaveLength(2);
    expect(screen.getByText(t('fleet.scheduling.alarmOffset', { minutes: 15 }))).toBeTruthy();

    // The alarm has no off state on the platform, so the table has no switch.
    expect(container.querySelectorAll('input[type="checkbox"]')).toHaveLength(0);
    expect(screen.getByText(t('fleet.scheduling.writeOnceNote'))).toBeTruthy();
  });

  it('reports the alarm the platform raised, and says whose app it rings', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    render(await SchedulingPage());

    // The alarm fired: fleet-svc wrote `alarmRaisedAt`, and the row says when.
    expect(
      screen.getByText(t('fleet.scheduling.alarmRang', { time: 'Jun 18, 2026, 6:40 AM' })),
    ).toBeTruthy();

    // Both of the bus's departures are covered by K. Fernando's open assignment.
    expect(
      screen.getAllByText(t('fleet.scheduling.ringsDriver', { driver: 'K. Fernando' })),
    ).toHaveLength(2);

    // The van's driver left on 10 June and the departure is on the 18th, so the
    // alarm would reach nobody — which is worth knowing before it does not.
    expect(screen.getByText(t('fleet.scheduling.ringsNobody'))).toBeTruthy();

    // And the sketch's own footnote about where it rings.
    expect(screen.getByText(t('fleet.scheduling.alarmNote', { grace: 30 }))).toBeTruthy();
  });

  it('says a route cannot be named, and shows the reference when there is one', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({
      ...FULL,
      '/schedules': {
        items: [{ ...SCHEDULES.items[0]!, routeId: '01JQR000000000000000000001' }],
      },
    });

    render(await SchedulingPage());

    expect(screen.getByText('01JQR000000000000000000001')).toBeTruthy();
    expect(screen.getAllByText(t('fleet.scheduling.routeNote')).length).toBeGreaterThan(0);
  });

  it('never names an organisation — every read is org-relative', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    await SchedulingPage();

    for (const [options] of read.mock.calls) {
      expect(Object.keys(options as object)).toEqual(['org']);
      expect((options as { org: string }).org).not.toContain('fleets');
    }
  });

  it('renders no booking form for a Viewer, and none for a pending organisation', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve(FULL);

    const viewer = render(await SchedulingPage());
    expect(screen.getByText(t('fleet.scheduling.viewerNotice'))).toBeTruthy();
    expect(viewer.container.querySelectorAll('form')).toHaveLength(0);

    cleanup();

    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));
    serve({ '/schedules': { items: [] }, '/vehicles': FORBIDDEN, '/assignments': FORBIDDEN });

    const pending = render(await SchedulingPage());
    expect(screen.getByText(t('fleet.scheduling.pendingOrg'))).toBeTruthy();
    expect(screen.getByText(t('fleet.scheduling.table.emptyPending'))).toBeTruthy();
    expect(pending.container.querySelectorAll('form')).toHaveLength(0);
  });

  it('offers only approved vehicles to a Manager, and books one in Colombo time', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    render(await SchedulingPage());

    // NC-1200 is PENDING: a vehicle nobody has approved cannot run a departure.
    const options = Array.from(
      (screen.getByRole('combobox') as HTMLSelectElement).options,
      (option) => option.textContent,
    );
    expect(options).toEqual(['NB-4521 · Bus', 'VN-8810 · Van']);

    const answer = await createSchedule(
      {},
      form({ vehicleId: BUS, departAt: '2026-12-18T06:00', notStartedAlarmMinutes: '15' }),
    );

    expect(answer.message).toBeUndefined();
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        org: '/schedules',
        // Six at the depot is 00:30 UTC — not 06:00, which is what a container
        // running in UTC would have sent.
        body: { vehicleId: BUS, departAt: '2026-12-18T00:30:00.000Z', notStartedAlarmMinutes: 15 },
        requires: { area: 'fleet-operations', requiresApprovedOrg: true },
      }),
    );
    expect(revalidatePath).toHaveBeenCalledWith('/scheduling');
  });

  it('refuses a departure that has already passed, on the field', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));

    const answer = await createSchedule({}, form({ vehicleId: BUS, departAt: '2020-01-01T06:00' }));

    expect(answer.field).toBe('departAt');
    expect(answer.message).toBe(t('fleet.scheduling.error.departAtPast'));
    expect(mutate).not.toHaveBeenCalled();
  });

  it('names no on-demand mode anywhere on the screen (AL-03)', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    const { container } = render(await SchedulingPage());
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-010
 * ------------------------------------------------------------------------ */

describe('SCR-FP-010 · billing & wallet', () => {
  const FULL = {
    '/billing': INVOICES,
    '/billing/i1': JUNE_DETAIL,
    '/billing/i2': MAY_DETAIL,
    '/billing/i2/receipt': RECEIPT,
    '/wallet': WALLET,
    '/vehicles': ROSTER,
  };

  const none = Promise.resolve({});

  it('draws the wireframe’s invoice table, whose lines sum to the total', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await BillingPage({ searchParams: none }));

    const invoice = screen.getByRole('region', { name: t('fleet.billing.invoice.label') });
    const headers = within(invoice)
      .getAllByRole('table')[0]!
      .querySelectorAll('th');

    expect(Array.from(headers, (header) => header.textContent)).toEqual([
      t('fleet.billing.column.item'),
      t('fleet.billing.column.qty'),
      t('fleet.billing.column.rate'),
      t('fleet.billing.column.amount'),
    ]);

    expect(within(invoice).getByText(t('fleet.billing.summary.modeB'))).toBeTruthy();
    // Two charged vehicles at Rs 34,500 each is the Rs 69,000 total.
    expect(within(invoice).getAllByText('Rs 34,500').length).toBeGreaterThan(0);
    expect(within(invoice).getAllByText('Rs 69,000').length).toBeGreaterThan(0);

    // And no reconciliation warning, because Σ lines = lineSumMinor = amountMinor.
    expect(within(invoice).queryByText(t('fleet.billing.reconcileWarning'))).toBeNull();
  });

  it('draws Mode A as free, out of the roster, and outside the total', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await BillingPage({ searchParams: none }));

    const invoice = screen.getByRole('region', { name: t('fleet.billing.invoice.label') });
    const row = within(invoice).getByText(t('fleet.billing.summary.modeA')).closest('tr')!;

    // Two Mode A vehicles on the roster, worth nothing, and not a line.
    expect(within(row).getByText('2')).toBeTruthy();
    expect(within(row).getByText('Rs 0')).toBeTruthy();

    // The invoice's own lines name no Mode A vehicle: NB-4521 is Mode A and is
    // not in the breakdown.
    const lines = within(invoice).getAllByRole('table')[1]!;
    expect(within(lines).queryByText('NB-4521')).toBeNull();
    expect(within(lines).getByText('VN-8810')).toBeTruthy();
    expect(within(lines).getByText(t('fleet.billing.line.firstMonthFree'))).toBeTruthy();

    expect(within(invoice).getByText(t('fleet.billing.modeANote'))).toBeTruthy();
  });

  it('says so when the lines do not add up to the total', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ ...FULL, '/billing/i1': { ...JUNE_DETAIL, lineSumMinor: 1 } });

    render(await BillingPage({ searchParams: none }));

    expect(screen.getByText(t('fleet.billing.reconcileWarning'))).toBeTruthy();
  });

  it('offers both of the platform’s documents, and the Pay verb for an open month', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await BillingPage({ searchParams: none }));

    const csv = screen.getByText(t('fleet.billing.download.csv'));
    const pdf = screen.getByText(t('fleet.billing.download.pdf'));

    expect(csv.getAttribute('href')).toBe('/billing/export?invoice=i1&format=csv');
    expect(pdf.getAttribute('href')).toBe('/billing/export?invoice=i1&format=pdf');
    expect(csv.hasAttribute('download')).toBe(true);

    expect(screen.getByText(t('fleet.billing.pay.submit'))).toBeTruthy();
  });

  it('opens a settled month with its receipt and no Pay button', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await BillingPage({ searchParams: Promise.resolve({ invoice: 'i2' }) }));

    expect(
      screen.getByText(
        t('fleet.billing.receipt.settled', { date: 'May 8, 2026, 10:00 AM', entry: 'j1' }),
      ),
    ).toBeTruthy();

    // `409 invoice-not-payable` is knowable before the press, so it is not drawn.
    expect(screen.queryByText(t('fleet.billing.pay.submit'))).toBeNull();
  });

  it('draws the wallet, its statement and two top-up rails — and no bank transfer', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    const { container } = render(await BillingPage({ searchParams: none }));
    const wallet = screen.getByRole('region', { name: t('fleet.billing.wallet.heading') });

    // Twice: the headline balance, and the balance the top-up left behind.
    expect(within(wallet).getAllByText('Rs 84,500')).toHaveLength(2);
    expect(within(wallet).getAllByText('Rs 69,000').length).toBeGreaterThan(0);
    expect(within(wallet).getByText('Rs 15,500')).toBeTruthy();

    // The statement the wallet route is described as answering for this screen.
    expect(within(wallet).getByText(t('fleet.billing.movement.topup'))).toBeTruthy();
    expect(within(wallet).getByText(t('fleet.billing.movement.invoice'))).toBeTruthy();

    const methods = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[name="method"]'),
      (input) => input.value,
    );
    expect(methods).toEqual(['onepay', 'lankaqr']);
    expect(within(wallet).getByText(t('fleet.billing.topup.noBankTransfer'))).toBeTruthy();

    // AL-05 as the absence it is: no control on the screen offers a third rail,
    // whatever the copy beside them says.
    const choices = Array.from(
      container.querySelectorAll<HTMLInputElement | HTMLOptionElement>('input, option'),
      (control) => control.value,
    );
    expect(choices.filter((value) => /transfer|bank|slip/i.test(value))).toEqual([]);
  });

  it('sends a top-up as integer minor units on a rail the contract admits', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    const answer = await topUpWallet({}, form({ amount: '50,000', method: 'lankaqr' }));

    expect(answer.message).toBeUndefined();
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        org: '/wallet/topup',
        body: { amountMinor: 5_000_000, method: 'lankaqr' },
        requires: { area: 'fleet-billing', requiresApprovedOrg: true },
      }),
    );
  });

  it('refuses an amount the service would refuse, before sending it', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    const answer = await topUpWallet({}, form({ amount: '10', method: 'onepay' }));

    expect(answer.field).toBe('amount');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('is a sentence for a Manager, and reads nothing at all', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve(FULL);

    render(await BillingPage({ searchParams: none }));

    expect(screen.getByText(t('fleet.billing.ownerOnly'))).toBeTruthy();
    expect(read).not.toHaveBeenCalled();
  });

  it('tells a pending organisation’s Owner why there is nothing to bill', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));
    serve(FULL);

    render(await BillingPage({ searchParams: none }));

    expect(screen.getByText(t('fleet.billing.pendingOrg'))).toBeTruthy();
    expect(read).not.toHaveBeenCalled();
  });

  it('lists the months and marks the one being shown', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    render(await BillingPage({ searchParams: Promise.resolve({ invoice: 'i2' }) }));

    const june = screen.getByText('June 2026');
    expect(june.getAttribute('href')).toBe('/billing?invoice=i1');
    expect(screen.getByText('May 2026').closest('tr')?.getAttribute('aria-selected')).toBe('true');
  });

  it('names no on-demand mode anywhere on the screen (AL-03)', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve(FULL);

    const { container } = render(await BillingPage({ searchParams: none }));
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });
});
