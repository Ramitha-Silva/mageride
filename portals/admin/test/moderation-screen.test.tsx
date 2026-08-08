import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { reportRows, type RenderContext } from '@/components/moderation/model';
import { ReportQueueTable } from '@/components/moderation/ReportQueueTable';
import { SuspendCard } from '@/components/moderation/SuspendCard';
import { ticketDetail, ticketRows } from '@/components/support/model';
import { TicketPanel } from '@/components/support/TicketPanel';
import { TicketQueueList } from '@/components/support/TicketQueueList';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest } from './support/urd';

/**
 * SCR-AP-004 and SCR-AP-005 as they are drawn — the Definition-of-Done items that
 * are properties of the rendered screen rather than of the model behind it:
 *
 *  - each screen sits at the path its **nav item** gives it, so the AL-06 gate the
 *    proxy applies is the one the page is behind;
 *  - a report row offers the two verdicts the platform will take, and a route to
 *    the suspension that is a separate decision;
 *  - the suspend card has **no duration control**, because nothing reinstates a
 *    suspension;
 *  - the ticket pane hands a refund **off** — a link, never a control that moves
 *    money — and only when the operator's menu carries the queue.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

// Both screens' controls call server actions on submit; nothing here submits, and
// importing the real modules would pull the data layer into a render test.
vi.mock('@/server/moderation-actions', () => ({
  decideReport: vi.fn(async () => ({})),
  suspendSubject: vi.fn(async () => ({})),
}));
vi.mock('@/server/support-actions', () => ({ resolveTicket: vi.fn(async () => ({})) }));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const VEHICLE = '0199a1f0-0000-7000-8000-0000000000a1';
const TICKET = '0199a1f0-0000-7000-8000-000000004521';
const USER = '0199a1f0-0000-7000-8000-000000090431';

describe('each screen is served at the path its nav item names', () => {
  // `routes.test.ts` holds `src/server/routes.ts` to `AdminMenu.cs`; this holds
  // the *files* to it. A page under any other path would be a screen the proxy
  // resolves to a different item's gate — or to no item, which is a 403.
  const paths = new Map(
    adminMenuManifest()
      .flatMap((group) => group.items)
      .map((item) => [item.key, item.path]),
  );

  it.each([
    ['reports', 'SCR-AP-004'],
    ['support-tickets', 'SCR-AP-005'],
  ])('%s (%s)', (key) => {
    const path = paths.get(key);
    expect(path, `AdminMenu.cs has no ${key} item`).toBeDefined();
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path!, 'page.tsx'))).toBe(true);
  });
});

describe('the vehicle-report queue', () => {
  const LABELS = {
    heading: 'Vehicle reports — pending review',
    caption: 'Vehicle reports awaiting a decision',
    rule: '3 confirmed reports delist the vehicle',
    subject: 'Subject',
    reports: 'Reports',
    reason: 'Reason',
    raised: 'Raised',
    action: 'Action',
    noReason: 'No reason was given',
    suspend: 'Suspend this vehicle',
    empty: 'No vehicle report is waiting on you.',
    decision: { confirm: 'Confirm report', dismiss: 'Dismiss', working: 'Recording…' },
  };

  const rows = reportRows(
    [
      {
        reportId: '0199a1f0-0000-7000-8000-000000000001',
        vehicleId: VEHICLE,
        reason: 'Rash driving',
        status: 'PENDING',
        createdAt: '2026-06-17T03:10:00Z',
      },
    ],
    context,
  );

  it('offers the two verdicts the platform takes, and names them after the vehicle', () => {
    render(<ReportQueueTable rows={rows} labels={LABELS} />);

    expect(screen.getByRole('button', { name: `Confirm the report against vehicle ${VEHICLE}` }))
      .toBeDefined();
    expect(screen.getByRole('button', { name: `Dismiss the report against vehicle ${VEHICLE}` }))
      .toBeDefined();
  });

  it('states the delisting rule instead of offering a button that claims to delist', () => {
    render(<ReportQueueTable rows={rows} labels={LABELS} />);

    expect(screen.getByText(LABELS.rule)).toBeDefined();
    expect(screen.queryByRole('button', { name: /delist/i })).toBeNull();
  });

  it('routes a suspension to the card, aimed at the row’s vehicle', () => {
    render(<ReportQueueTable rows={rows} labels={LABELS} />);

    expect(screen.getByRole('link', { name: LABELS.suspend }).getAttribute('href')).toBe(
      `/reports?subject=vehicle&subjectId=${VEHICLE}#suspend`,
    );
  });

  it('says the queue is empty rather than drawing a row nobody reported', () => {
    render(<ReportQueueTable rows={[]} labels={LABELS} />);

    expect(screen.getByText(LABELS.empty)).toBeDefined();
  });
});

describe('the suspend card', () => {
  const LABELS = {
    heading: 'Suspend / ban',
    subject: 'Suspend a',
    driver: 'Driver',
    vehicle: 'Vehicle',
    subjectId: 'Driver / vehicle ID',
    subjectIdHint: 'The platform id, exactly as it appears on the record.',
    reason: 'Reason',
    reasonHint: 'Required, and recorded against your name.',
    apply: 'Apply',
    working: 'Recording…',
    noDuration: 'A suspension stays in force until somebody lifts it.',
    audit: 'This action is written to the audit trail against your name.',
    suspendedDriver: 'Driver suspended.',
    suspendedVehicle: 'Vehicle suspended.',
    recordedDriver: 'Recorded as DRIVER_SUSPENDED.',
    recordedVehicle: 'Recorded as VEHICLE_SUSPENDED.',
  };

  it('has no duration control, because nothing reinstates a suspension', () => {
    render(<SuspendCard subject="driver" subjectId="" labels={LABELS} />);

    expect(screen.queryByLabelText(/duration/i)).toBeNull();
    expect(screen.getByText(LABELS.noDuration)).toBeDefined();
  });

  it('is aimed by the URL, so the row that sent the operator here is already in the box', () => {
    render(<SuspendCard subject="vehicle" subjectId={VEHICLE} labels={LABELS} />);

    expect(screen.getByLabelText(LABELS.subjectId).getAttribute('value')).toBe(VEHICLE);
    expect((screen.getByLabelText(LABELS.subject) as HTMLSelectElement).value).toBe('vehicle');
  });

  it('says the action is audited before it is taken', () => {
    render(<SuspendCard subject="driver" subjectId="" labels={LABELS} />);

    expect(screen.getByText(LABELS.audit)).toBeDefined();
  });
});

describe('the ticket queue and the ticket', () => {
  const row = {
    ticketId: TICKET,
    userId: USER,
    category: 'driver_qr_dispute',
    status: 'OPEN' as const,
    description: 'The transfer never arrived.',
    createdAt: '2026-06-17T03:10:00Z',
  };

  const PANEL_LABELS = {
    raisedBy: 'Raised by',
    threadHeading: 'Thread',
    threadEmpty: 'This ticket carries no message.',
    lookupHeading: 'Read-only lookup',
    lookupNote: 'A directory record is read-only.',
    lookupNone: 'The directories are not part of your role.',
    refundHeading: 'Refund request',
    refundNote: 'Support does not move money.',
    refundLink: 'Open the refunds queue',
    resolvedHeading: 'Resolved',
    resolvedNote: 'This ticket is closed.',
    resolve: {
      response: 'Your reply',
      responseHint: 'Shown to the person who raised the ticket, exactly as you write it.',
      resolve: 'Resolve',
      working: 'Recording…',
      audit: 'This action is written to the audit trail against your name.',
    },
  };

  it('marks the ticket being read, for a screen reader as well as a sighted operator', () => {
    render(
      <TicketQueueList
        rows={ticketRows([row, { ...row, ticketId: '0199a1f0-0000-7000-8000-000000004519' }], { ticketId: TICKET }, context)}
        labels={{ heading: 'Queue', empty: 'No ticket matches this filter.' }}
      />,
    );

    const current = screen
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.getAttribute('href')).toContain(TICKET);
  });

  it('hands a refund off to Finance as a link, and offers no control that moves money', () => {
    render(
      <TicketPanel
        detail={ticketDetail(row, context)}
        status=""
        category=""
        lookups={[]}
        refundHref="/finance/refunds"
        labels={PANEL_LABELS}
      />,
    );

    const refund = screen.getByRole('link', { name: PANEL_LABELS.refundLink });
    expect(refund.getAttribute('href')).toBe('/finance/refunds');
    expect(screen.queryByRole('button', { name: /refund/i })).toBeNull();
    expect(screen.getByText(PANEL_LABELS.refundNote)).toBeDefined();
  });

  it('draws no refund link at all when the operator’s menu has no refunds item', () => {
    render(
      <TicketPanel
        detail={ticketDetail(row, context)}
        status=""
        category=""
        lookups={[]}
        refundHref={null}
        labels={PANEL_LABELS}
      />,
    );

    expect(screen.queryByRole('link', { name: PANEL_LABELS.refundLink })).toBeNull();
    expect(screen.getByText(PANEL_LABELS.refundNote)).toBeDefined();
  });

  it('offers the reply box on an open ticket and closes it on a resolved one', () => {
    const { unmount } = render(
      <TicketPanel
        detail={ticketDetail(row, context)}
        status=""
        category=""
        lookups={[]}
        refundHref={null}
        labels={PANEL_LABELS}
      />,
    );

    expect(screen.getByLabelText(PANEL_LABELS.resolve.response)).toBeDefined();
    // support-svc has a reply-without-closing route; admin-bff exposes none, so
    // the wireframe's Reply button is deliberately absent rather than dead.
    expect(screen.queryByRole('button', { name: /^reply$/i })).toBeNull();
    unmount();

    render(
      <TicketPanel
        detail={ticketDetail({ ...row, status: 'RESOLVED', response: 'Refunded.' }, context)}
        status=""
        category=""
        lookups={[]}
        refundHref={null}
        labels={PANEL_LABELS}
      />,
    );

    expect(screen.queryByLabelText(PANEL_LABELS.resolve.response)).toBeNull();
    expect(screen.getByText(PANEL_LABELS.resolvedNote)).toBeDefined();
  });

  it('draws the raiser’s words and the agent’s as a thread', () => {
    render(
      <TicketPanel
        detail={ticketDetail({ ...row, status: 'RESOLVED', response: 'Refunded.', resolvedAt: '2026-06-17T05:00:00Z' }, context)}
        status=""
        category=""
        lookups={[]}
        refundHref={null}
        labels={PANEL_LABELS}
      />,
    );

    const thread = screen.getByRole('list');
    expect(within(thread).getByText('The transfer never arrived.')).toBeDefined();
    expect(within(thread).getByText('Refunded.')).toBeDefined();
  });
});
