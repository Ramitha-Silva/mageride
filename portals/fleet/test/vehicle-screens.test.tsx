import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ProblemError } from '@/api/problem';
import type { PayoutProfile } from '@/api/payout';
import type { FleetVehicle, VehicleDocumentSlot } from '@/api/vehicles';
import { createFleetTranslator } from '@/i18n';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-004, SCR-FP-005 and SCR-FP-006 on the rendered page** — the
 * component's Definition of Done, where an operator would meet it.
 *
 * Four items cannot be checked anywhere else:
 *
 *  1. a Mode A vehicle cannot reach Approved while the route permit is Missing or
 *     Pending, **and the screen says which slot is holding it**;
 *  2. selecting Paid is blocked with a clear message while the payout profile is
 *     unverified;
 *  3. the status table carries the Documents and Service payment columns;
 *  4. a Viewer session renders no mutating control on any of the three.
 */

const redirected = new Error('NEXT_REDIRECT');

const redirect = vi.fn(() => {
  throw redirected;
});
const getSession = vi.fn();
const read = vi.fn();

vi.mock('next/navigation', () => ({
  redirect: () => redirect(),
  permanentRedirect: () => redirect(),
  forbidden: () => {
    throw new Error('NEXT_FORBIDDEN');
  },
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('next/cache', () => ({ revalidatePath: vi.fn() }));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/api/client', () => ({ read: (options: unknown) => read(options), mutate: vi.fn() }));

// The three action modules the client panels bind their forms to. A server
// action is a function to `useActionState`, so a stub is enough to render one.
vi.mock('@/server/vehicle-actions', () => ({
  addVehicle: vi.fn(),
  setServicePayment: vi.fn(),
  uploadVehicleDocument: vi.fn(),
  importVehicleCsv: vi.fn(),
  readBulkJob: vi.fn(),
}));
vi.mock('@/server/driver-actions', () => ({
  assignDriver: vi.fn(),
  revokeAssignment: vi.fn(),
}));
vi.mock('@/server/tracker-actions', () => ({
  bindTracker: vi.fn(),
  importTrackerCsv: vi.fn(),
  readTrackerBulkJob: vi.fn(),
}));

const { default: VehiclesPage } = await import('../app/(portal)/vehicles/page');
const { default: DriversPage } = await import('../app/(portal)/drivers/page');
const { default: TrackersPage } = await import('../app/(portal)/trackers/page');
const { importVehicleCsv } = await import('@/server/vehicle-actions');

const t = createFleetTranslator('en');

/* ---------------------------------------------------------------------------
 * Fixtures
 * ------------------------------------------------------------------------ */

const BUS: FleetVehicle = {
  vehicleId: '01JQV000000000000000000001',
  registrationNumber: 'NB-4521',
  vehicleType: 'bus',
  mode: 'A',
  status: 'PENDING',
  docsStatus: 'docs_pending',
};

const VAN: FleetVehicle = {
  vehicleId: '01JQV000000000000000000002',
  registrationNumber: 'VN-8810',
  vehicleType: 'van',
  mode: 'B',
  status: 'APPROVED',
  docsStatus: 'docs_complete',
  modeBBilling: 'paid',
  defaultMonthlyFareMinor: 600_000,
  currency: 'LKR',
};

const ROSTER = { items: [BUS, VAN] };

/** A Mode A vehicle's four slots, with the route permit in whatever state. */
function busSlots(permit: VehicleDocumentSlot['status']): { items: VehicleDocumentSlot[] } {
  return {
    items: [
      { kind: 'registration', status: 'verified', required: true },
      { kind: 'insurance', status: 'verified', required: true },
      { kind: 'revenue_license', status: 'verified', required: true },
      { kind: 'permit', status: permit, required: true },
    ],
  };
}

function payout(status: PayoutProfile['status']): PayoutProfile {
  return {
    bank: 'Commercial Bank of Ceylon',
    branch: 'Nugegoda',
    accountNo: '8001234567',
    accountHolderName: 'Lanka Transit (Pvt) Ltd',
    status,
  };
}

/** Routes each `read({org})` to its fixture, so one mock serves every screen. */
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

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);

/* ---------------------------------------------------------------------------
 * SCR-FP-004
 * ------------------------------------------------------------------------ */

describe('SCR-FP-004 · vehicle onboarding', () => {
  it('draws the status table the wireframe draws, with Documents and Service payment', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ '/vehicles': ROSTER, '/payout-profile': payout('verified') });

    render(await VehiclesPage({ searchParams: Promise.resolve({}) }));

    const table = screen.getByRole('table');
    const headers = within(table)
      .getAllByRole('columnheader')
      .map((header) => header.textContent);

    expect(headers).toContain(t('fleet.vehicles.column.documents'));
    expect(headers).toContain(t('fleet.vehicles.column.servicePayment'));

    // A Mode A vehicle has no service payment at all — the wireframe's dash.
    expect(within(table).getByText('NB-4521')).toBeTruthy();
    expect(within(table).getByText(t('fleet.vehicles.servicePayment.notApplicable'))).toBeTruthy();

    // A Paid Mode B vehicle prints its default monthly fare, in rupees.
    expect(within(table).getByText('Paid · Rs 6,000/mo')).toBeTruthy();
    expect(within(table).getByText(t('fleet.vehicles.docsCell.pending'))).toBeTruthy();
    expect(within(table).getByText(t('fleet.vehicles.docsCell.complete'))).toBeTruthy();
  });

  it('names no on-demand mode anywhere on the screen (AL-03)', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ '/vehicles': ROSTER, '/payout-profile': payout('verified') });

    const { container } = render(await VehiclesPage({ searchParams: Promise.resolve({}) }));

    expect(screen.getByText(t('fleet.vehicles.modesOnly'))).toBeTruthy();
    expect(container.textContent ?? '').not.toMatch(/\bmode[\s_-]?c\b/i);
  });

  it('draws AL-50’s four named slots and no generic dropzone', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({
      '/vehicles': ROSTER,
      '/payout-profile': payout('verified'),
      [`/vehicles/${BUS.vehicleId}/documents`]: busSlots('missing'),
    });

    render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: BUS.vehicleId }) }));

    for (const name of [
      t('fleet.vehicles.doc.registration'),
      t('fleet.vehicles.doc.insurance'),
      t('fleet.vehicles.doc.revenueLicense'),
      t('fleet.vehicles.doc.routePermit'),
    ]) {
      expect(screen.getByRole('heading', { name }), name).toBeTruthy();
    }

    // Four boxes and four only — a fifth would mean a slot arrived from the wire.
    expect(screen.getAllByText(t('fleet.vehicles.doc.upload'))).toHaveLength(4);
  });

  it('holds a Mode A vehicle out of Approved while the route permit is Missing or Pending', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    for (const status of ['missing', 'pending'] as const) {
      serve({
        '/vehicles': ROSTER,
        '/payout-profile': payout('verified'),
        [`/vehicles/${BUS.vehicleId}/documents`]: busSlots(status),
      });

      render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: BUS.vehicleId }) }));

      expect(screen.getByText(t('fleet.vehicles.docs.approvalGate'))).toBeTruthy();
      // The screen names the slot rather than colouring a chip.
      expect(
        screen.getByText(
          `Waiting on: ${t('fleet.vehicles.doc.routePermit')} (${
            status === 'missing'
              ? t('fleet.vehicles.slot.missing')
              : t('fleet.vehicles.slot.pending')
          }).`,
        ),
        status,
      ).toBeTruthy();
      expect(screen.queryByText(t('fleet.vehicles.docs.ready'))).toBeNull();

      cleanup();
    }

    serve({
      '/vehicles': ROSTER,
      '/payout-profile': payout('verified'),
      [`/vehicles/${BUS.vehicleId}/documents`]: busSlots('verified'),
    });

    render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: BUS.vehicleId }) }));
    expect(screen.getByText(t('fleet.vehicles.docs.ready'))).toBeTruthy();
  });

  it('blocks Paid with the payout screen’s own sentence while the profile is unverified', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({
      '/vehicles': ROSTER,
      '/payout-profile': payout('pending_verification'),
      [`/vehicles/${VAN.vehicleId}/documents`]: { items: [] },
    });

    render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: VAN.vehicleId }) }));

    // The Service payment control for the selected Mode B vehicle.
    const paid = screen
      .getAllByRole('option', { name: t('fleet.vehicles.servicePayment.paid') })
      .at(0);
    expect(paid?.hasAttribute('disabled')).toBe(true);

    // One rule, one sentence — `PAID_SERVICE_PAYMENT_BLOCKED_KEY`'s, the same
    // words SCR-FP-002a puts under its status chip.
    expect(screen.getAllByText(t('fleet.payout.gate.paid')).length).toBeGreaterThan(0);
  });

  it('leaves Paid available once an officer has verified the profile', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({
      '/vehicles': ROSTER,
      '/payout-profile': payout('verified'),
      [`/vehicles/${VAN.vehicleId}/documents`]: { items: [] },
    });

    render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: VAN.vehicleId }) }));

    const paid = screen
      .getAllByRole('option', { name: t('fleet.vehicles.servicePayment.paid') })
      .at(0);
    expect(paid?.hasAttribute('disabled')).toBe(false);
    expect(screen.queryByText(t('fleet.payout.gate.paid'))).toBeNull();
  });

  it('does not read the Owner-only payout profile for a Manager', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/vehicles': ROSTER });

    render(await VehiclesPage({ searchParams: Promise.resolve({}) }));

    const targets = read.mock.calls.map((call) => (call[0] as { org?: string }).org);
    expect(targets).not.toContain('/payout-profile');
    // …and the option is not pre-refused on a fact this session cannot check.
    // The label carries the required marker, so it is matched by substring.
    fireEvent.change(screen.getByLabelText(t('fleet.vehicles.field.mode'), { exact: false }), {
      target: { value: 'B' },
    });
    const paid = screen
      .getAllByRole('option', { name: t('fleet.vehicles.servicePayment.paid') })
      .at(0);
    expect(paid?.hasAttribute('disabled')).toBe(false);
  });

  it('offers the bulk CSV with its columns, its cap and the docs-pending consequence', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ '/vehicles': ROSTER, '/payout-profile': payout('verified') });

    render(await VehiclesPage({ searchParams: Promise.resolve({}) }));

    // Radix activates a tab on focus (`activationMode` defaults to automatic),
    // so a bare click does not switch the panel.
    fireEvent.focus(screen.getByRole('tab', { name: t('fleet.vehicles.tab.bulk') }));

    expect(screen.getByText(t('fleet.vehicles.bulk.prompt'))).toBeTruthy();
    expect(screen.getByText(/registrationNumber,vehicleType,mode/)).toBeTruthy();
    expect(screen.getByText(t('fleet.vehicles.bulk.docsPending'))).toBeTruthy();
  });

  it('imports the good rows of a bad CSV and offers the error report', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    serve({ '/vehicles': ROSTER, '/payout-profile': payout('verified') });

    // `COMPLETED` with `failedRows > 0` is a partial import, not a failure —
    // `fleet.yaml` says so in as many words, and this is that state rendered.
    vi.mocked(importVehicleCsv).mockResolvedValue({
      job: {
        jobId: '01JQJ000000000000000000001',
        totalRows: 120,
        importedRows: 118,
        failedRows: 2,
        status: 'COMPLETED',
        errorReportUrl: 'https://api.mageride.lk/v1/fleets/x/vehicles/bulk/y/errors.csv?sig=z',
      },
    });

    const { container } = render(await VehiclesPage({ searchParams: Promise.resolve({}) }));
    fireEvent.focus(screen.getByRole('tab', { name: t('fleet.vehicles.tab.bulk') }));

    const picker = container.querySelector('input[type="file"]');
    expect(picker).toBeTruthy();
    fireEvent.change(picker!, {
      target: { files: [new File(['NB-4521,bus,A\n'], 'fleet.csv', { type: 'text/csv' })] },
    });

    await waitFor(() =>
      expect(
        screen.getByText(t('fleet.vehicles.bulk.imported', { imported: 118, total: 120 })),
      ).toBeTruthy(),
    );

    expect(screen.getByText(t('fleet.vehicles.bulk.someFailed', { failed: 2 }))).toBeTruthy();

    // The link is handed to the browser as it is: the HMAC in the query string
    // *is* the credential, so no bearer travels with it.
    const report = screen.getByRole('link', { name: t('fleet.vehicles.bulk.report') });
    expect(report.getAttribute('href')).toContain('errors.csv');
    expect(report.hasAttribute('download')).toBe(true);
  });

  it('renders no mutating control for a Viewer', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({ '/vehicles': ROSTER });

    render(await VehiclesPage({ searchParams: Promise.resolve({ vehicle: VAN.vehicleId }) }));

    expect(screen.queryByRole('button', { name: t('fleet.vehicles.add.submit') })).toBeNull();
    expect(
      screen.queryByRole('button', { name: t('fleet.vehicles.servicePayment.save') }),
    ).toBeNull();
    expect(screen.getAllByText(t('fleet.vehicles.viewerNotice')).length).toBeGreaterThan(0);
    // The roster is still there — a Viewer is defined as read-only monitoring.
    expect(screen.getByRole('table')).toBeTruthy();
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-005
 * ------------------------------------------------------------------------ */

describe('SCR-FP-005 · driver assignment', () => {
  const ASSIGNMENTS = {
    items: [
      {
        assignmentId: '01JQA000000000000000000001',
        driverId: '01JQD000000000000000000001',
        driverName: 'K. Fernando',
        driverPhone: '+94771234567',
        vehicleId: BUS.vehicleId,
        registrationNumber: 'NB-4521',
        from: '2026-06-02T00:00:00Z',
        active: true,
      },
      {
        assignmentId: '01JQA000000000000000000002',
        driverId: '01JQD000000000000000000002',
        vehicleId: VAN.vehicleId,
        registrationNumber: 'VN-8810',
        from: '2026-05-01T00:00:00Z',
        to: '2026-05-31T00:00:00Z',
        revokedAt: '2026-05-20T00:00:00Z',
        active: false,
      },
    ],
  };

  it('assigns by User ID or phone across one or more vehicles, with a window', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/assignments': ASSIGNMENTS, '/vehicles': ROSTER });

    render(await DriversPage());

    // The driver field is required, so its label carries the marker too.
    expect(screen.getByLabelText(t('fleet.drivers.field.driver'), { exact: false })).toBeTruthy();
    expect(screen.getByLabelText(t('fleet.drivers.field.from'))).toBeTruthy();
    expect(screen.getByLabelText(t('fleet.drivers.field.to'))).toBeTruthy();
    // One driver, several vehicles (US-13.2).
    expect(screen.getAllByRole('checkbox')).toHaveLength(2);
    // AL-23's temporary hire, said beside the field.
    expect(screen.getByText(t('fleet.drivers.temporary'))).toBeTruthy();
  });

  it('keeps revoked and expired rows, because the history is the screen', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/assignments': ASSIGNMENTS, '/vehicles': ROSTER });

    render(await DriversPage());

    const table = screen.getByRole('table');
    expect(within(table).getByText('K. Fernando')).toBeTruthy();
    expect(within(table).getByText(t('fleet.drivers.status.active'))).toBeTruthy();
    expect(within(table).getByText(t('fleet.drivers.status.revoked'))).toBeTruthy();

    // Only the standing assignment can be ended; the revoked row's button would
    // only ever answer 404.
    expect(within(table).getAllByRole('button', { name: t('fleet.drivers.revoke') })).toHaveLength(
      1,
    );
  });

  it('says how a driver comes to exist rather than drawing an invite that posts nowhere', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/assignments': ASSIGNMENTS, '/vehicles': ROSTER });

    render(await DriversPage());

    expect(screen.getByText(t('fleet.drivers.noInvite'))).toBeTruthy();
    expect(screen.queryByRole('button', { name: /resend/i })).toBeNull();
  });

  it('renders no mutating control for a Viewer', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({ '/assignments': ASSIGNMENTS, '/vehicles': ROSTER });

    render(await DriversPage());

    expect(screen.queryByRole('button', { name: t('fleet.drivers.assign.submit') })).toBeNull();
    expect(screen.queryByRole('button', { name: t('fleet.drivers.revoke') })).toBeNull();
    expect(screen.getByText(t('fleet.drivers.viewerNotice'))).toBeTruthy();
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-006
 * ------------------------------------------------------------------------ */

describe('SCR-FP-006 · tracker binding', () => {
  const HEALTH = {
    fleetId: '01JQF0000000000000000000FL',
    vehiclesOnline: 1,
    vehiclesOffline: 1,
    counts: { online: 1, stale: 0, offline: 1, decommissioned: 1, total: 3 },
    thresholds: { staleAfterSeconds: 300, offlineAfterSeconds: 1800 },
    items: [
      {
        vehicleId: BUS.vehicleId,
        imei: '861234567890123',
        state: 'online',
        online: true,
        lastSeen: new Date().toISOString(),
      },
      {
        vehicleId: VAN.vehicleId,
        imei: '861234567897788',
        state: 'decommissioned',
        online: false,
      },
    ],
    itemsTruncated: false,
    asOf: '2026-08-08T04:30:00Z',
  };

  it('draws the wireframe’s columns plus the credential the health rollup makes possible', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/health': HEALTH, '/vehicles': ROSTER });

    render(await TrackersPage());

    const headers = within(screen.getByRole('table'))
      .getAllByRole('columnheader')
      .map((header) => header.textContent);

    expect(headers).toEqual([
      t('fleet.trackers.column.imei'),
      t('fleet.trackers.column.vehicle'),
      t('fleet.trackers.column.cadence'),
      t('fleet.trackers.column.lastSeen'),
      t('fleet.trackers.column.health'),
      t('fleet.trackers.column.credential'),
    ]);

    expect(screen.getByText('861234567890123')).toBeTruthy();
    expect(screen.getByText(t('fleet.trackers.state.decommissioned'))).toBeTruthy();
    // …and that state read as a credential rather than as a signal problem.
    expect(screen.getByText(t('fleet.trackers.credential.revoked'))).toBeTruthy();
  });

  it('reports the cadence as a fact and says why it is not a control', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({ '/health': HEALTH, '/vehicles': ROSTER });

    render(await TrackersPage());

    expect(screen.getAllByText('4 s moving · 10 s stationary').length).toBeGreaterThan(0);
    expect(screen.getByText(t('fleet.trackers.cadenceNote'))).toBeTruthy();
  });

  it('labels the legend with the deployment’s own thresholds', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));
    serve({
      '/health': { ...HEALTH, thresholds: { staleAfterSeconds: 120, offlineAfterSeconds: 600 } },
      '/vehicles': ROSTER,
    });

    render(await TrackersPage());

    expect(
      screen.getByText(t('fleet.trackers.thresholds', { stale: 2, offline: 10 })),
    ).toBeTruthy();
  });

  it('opens for a pending organisation and replaces the bind form with the reason', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));
    serve({ '/health': HEALTH, '/vehicles': ROSTER });

    render(await TrackersPage());

    // The screen is not approval-gated — the health read is not — but the single
    // bind is, so the form is the sentence that says when it opens.
    expect(screen.getByRole('table')).toBeTruthy();
    expect(screen.queryByRole('button', { name: t('fleet.trackers.bind.submit') })).toBeNull();
    expect(screen.getByText(t('fleet.trackers.bind.pendingOrg'))).toBeTruthy();
  });

  it('renders no mutating control for a Viewer', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    serve({ '/health': HEALTH, '/vehicles': ROSTER });

    render(await TrackersPage());

    expect(screen.queryByRole('button', { name: t('fleet.trackers.bind.submit') })).toBeNull();
    expect(screen.queryByText(t('fleet.trackers.bulk.heading'))).toBeNull();
    expect(screen.getByText(t('fleet.trackers.viewerNotice'))).toBeTruthy();
  });
});
