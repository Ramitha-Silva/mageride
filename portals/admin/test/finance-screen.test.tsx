import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { ExceptionQueueTable } from '@/components/finance/ExceptionQueueTable';
import { exceptionRows, refundRows, settlementRails, settlementTotals, type RenderContext } from '@/components/finance/model';
import { PayoutsPanel } from '@/components/finance/PayoutsPanel';
import { RefundQueueTable } from '@/components/finance/RefundQueueTable';
import { ReverseFeeCard } from '@/components/finance/ReverseFeeCard';
import { SettlementCard } from '@/components/finance/SettlementCard';
import { ScreenTabs } from '@/components/ScreenTabs';
import { createAdminTranslator } from '@/i18n';

import { adminMenuManifest } from './support/urd';

/**
 * SCR-AP-006 as it is drawn — the wireframe items that are properties of the
 * rendered screen, and the three the wireframe draws that this platform cannot
 * honour.
 *
 * The absences are the interesting half. The sketch shows a settlement table that
 * could hold a bank-transfer row (AL-05 says it cannot), a reversal card with one
 * "Driver" field (a daily-fee charge is keyed on three things), and a Payouts tab
 * with no failure handling (C133 removed the retry route because a failed
 * instruction has already been reversed).
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('@/server/finance-actions', () => ({
  raiseRefund: vi.fn(async () => ({})),
  reverseFee: vi.fn(async () => ({})),
}));

afterEach(cleanup);

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const DRIVER = '0199a1f0-0000-7000-8000-0000000000d1';
const PAYMENT = '0199a1f0-0000-7000-8000-0000000000a1';

describe('every finance screen sits at the path its nav item names', () => {
  const paths = new Map(
    adminMenuManifest()
      .flatMap((group) => group.items)
      .map((item) => [item.key, item.path]),
  );

  it.each([
    ['reconciliation', 'SCR-AP-006 · gateway settlement'],
    ['transactions', 'SCR-AP-006 · wallet ledger'],
    ['refunds', 'SCR-AP-006 · refunds'],
    ['wallet-adjustments', 'SCR-AP-006 · wallet reversals'],
  ])('%s (%s)', (key) => {
    const path = paths.get(key);
    expect(path, `AdminMenu.cs has no ${key} item`).toBeDefined();
    expect(existsSync(join(APP_ROOT, 'app/(portal)', path!, 'page.tsx'))).toBe(true);
  });

  it('serves both exports under the transactions screen, so they share its gate', () => {
    // Under the item's own path, so `resolveRoute` gates the downloads on the same
    // nav entry as the page — no entry in `routes.ts` and no exemption.
    expect(
      existsSync(join(APP_ROOT, 'app/(portal)/finance/transactions/export/csv/route.ts')),
    ).toBe(true);
    expect(
      existsSync(join(APP_ROOT, 'app/(portal)/finance/transactions/export/pdf/route.ts')),
    ).toBe(true);
  });
});

describe('the settlement card', () => {
  const LABELS = {
    heading: 'OnePay / LankaQR settlement reconciliation',
    caption: 'Gateway settlement against the platform ledger',
    window: 'Window:',
    gateway: 'Gateway',
    sessions: 'Settled top-ups',
    settled: 'Confirmed by gateway',
    posted: 'Reached the ledger',
    variance: 'Difference',
    action: 'Action',
    investigate: 'Investigate',
    empty: 'Neither gateway settled anything in this window.',
    noBankTransfer: 'There is no bank-transfer rail to reconcile',
    totals: 'Every figure is read from the double-entry ledger',
  };

  const summary = {
    from: '2026-06-01',
    to: '2026-06-30',
    settledMinor: 18_500_00,
    postedMinor: 18_300_00,
    varianceMinor: 200_00,
    exceptionCount: 2,
    days: [
      {
        businessDate: '2026-06-17',
        method: 'onepay' as const,
        openedCount: 8_120,
        settledCount: 8_120,
        failedCount: 0,
        pendingCount: 0,
        settledMinor: 12_400_00,
        postedMinor: 12_400_00,
        varianceMinor: 0,
        currency: 'LKR',
      },
      {
        businessDate: '2026-06-17',
        method: 'lankaqr' as const,
        openedCount: 5_402,
        settledCount: 5_402,
        failedCount: 0,
        pendingCount: 0,
        settledMinor: 6_100_00,
        postedMinor: 5_900_00,
        varianceMinor: 200_00,
        currency: 'LKR',
      },
    ],
  };

  function draw() {
    return render(
      <SettlementCard
        totals={settlementTotals(summary, context)}
        rails={settlementRails(summary.days, context)}
        labels={LABELS}
      />,
    );
  }

  it('draws the wireframe’s columns — gateway, settled, ledger, Δ and an action', () => {
    draw();

    for (const column of [LABELS.gateway, LABELS.settled, LABELS.posted, LABELS.variance]) {
      expect(screen.getByRole('columnheader', { name: column })).toBeDefined();
    }
  });

  it('draws exactly the two rails there are, and states that there is no third (AL-05)', () => {
    draw();

    expect(screen.getByText('OnePay')).toBeDefined();
    expect(screen.getByText('LankaQR')).toBeDefined();
    expect(screen.queryByText(/bank transfer/i)).toBeNull();
    expect(screen.getByText(LABELS.noBankTransfer)).toBeDefined();
  });

  it('does not print the +5% surcharge the wireframe still shows beside OnePay', () => {
    // AL-57/AL-59 removed the rail's ride-payment role and the surcharge with it.
    // Printing it would describe a fee nothing charges.
    draw();
    expect(screen.queryByText(/\+5\s*%/)).toBeNull();
  });

  it('aims each Investigate link at its own rail’s exceptions', () => {
    draw();

    const links = screen.getAllByRole('link');
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '/finance/reconciliation?method=onepay',
      '/finance/reconciliation?method=lankaqr',
    ]);
  });

  it('marks a reconciled rail and an out-of-balance one differently', () => {
    draw();

    expect(screen.getByText('Reconciled')).toBeDefined();
    // Signed, and to the cent: a two-cent variance is still a variance, so the
    // finance surface does not round to whole rupees the way a KPI card does.
    // Twice: once on the card's total and once on the rail that caused it.
    expect(screen.getAllByText(/\+200\.00/)).toHaveLength(2);
  });
});

describe('the exception queue offers nothing to close', () => {
  it('draws rows and no control', () => {
    // Membership is derived from the row, so a session that resolves itself leaves
    // on its own. A "dismiss" button would be a second source of truth.
    render(
      <ExceptionQueueTable
        rows={exceptionRows(
          [
            {
              topupId: '0199a1f0-0000-7000-8000-0000000000e1',
              kind: 'settled-not-posted',
              method: 'onepay',
              state: 'Succeeded',
              driverId: DRIVER,
              amountMinor: 5_000_00,
              currency: 'LKR',
              createdAt: '2026-06-17T03:10:00Z',
            },
          ],
          context,
        )}
        labels={{
          heading: 'Settlement exceptions',
          caption: 'Gateway sessions that need a person',
          note: 'Worked oldest first.',
          kind: 'What happened',
          gateway: 'Gateway',
          driver: 'Driver',
          amount: 'Amount',
          posted: 'Reached the ledger',
          opened: 'Opened',
          reference: 'Gateway reference',
          empty: 'Nothing is waiting.',
        }}
      />,
    );

    expect(screen.getByText('Settled, never posted')).toBeDefined();
    expect(screen.queryAllByRole('button')).toHaveLength(0);
    expect(screen.queryAllByRole('link')).toHaveLength(0);
  });
});

describe('the wallet-reversal card', () => {
  const LABELS = {
    heading: 'Wallet reversal / adjustment',
    note: 'Puts a daily fee back on a driver’s wallet.',
    driver: 'Driver ID',
    driverHint: 'The platform ID.',
    vehicle: 'Vehicle ID',
    vehicleHint: 'The vehicle the fee was charged for.',
    feeDate: 'Day of the fee',
    feeDateHint: 'Sri Lanka time.',
    amount: 'Amount',
    amountHint: 'Leave empty for the whole fee.',
    reason: 'Reason',
    reasonHint: 'Required.',
    submit: 'Post the reversal',
    working: 'Posting…',
    audit: 'This action is written to the audit trail against your name.',
    done: 'Reversal posted.',
    replayed: 'This fee had already been reversed.',
    balanceAfter: 'Wallet balance now:',
    recorded: 'Recorded in the audit trail as WALLET_FEE_REVERSED.',
  };

  it('names all three parts of a charge, not just the driver the wireframe draws', () => {
    render(<ReverseFeeCard driverId="" labels={LABELS} />);

    expect(screen.getByLabelText(LABELS.driver)).toBeDefined();
    expect(screen.getByLabelText(LABELS.vehicle)).toBeDefined();
    expect(screen.getByLabelText(LABELS.feeDate)).toBeDefined();
  });

  it('is aimed by the URL, so the record that sent the operator here is in the box', () => {
    render(<ReverseFeeCard driverId={DRIVER} labels={LABELS} />);
    expect(screen.getByLabelText(LABELS.driver).getAttribute('value')).toBe(DRIVER);
  });

  it('says the action is audited before it is taken', () => {
    render(<ReverseFeeCard driverId="" labels={LABELS} />);
    expect(screen.getByText(LABELS.audit)).toBeDefined();
  });
});

describe('the refund queue and who gets its button', () => {
  const rows = refundRows(
    [
      {
        source: 'overpaid',
        paymentId: PAYMENT,
        rideId: '0199a1f0-0000-7000-8000-0000000000r1',
        paymentState: 'Overpaid',
        method: 'wallet',
        amountMinor: 1_200_00,
        paymentAmountMinor: 1_200_00,
        currency: 'LKR',
        requestedAt: '2026-06-17T03:10:00Z',
      },
    ],
    {},
    context,
  );

  const LABELS = {
    heading: 'Refund queue',
    caption: 'Refunds awaiting settlement',
    note: 'Two kinds of row.',
    columnSource: 'Kind',
    columnPassenger: 'Passenger',
    columnPayment: 'Payment',
    columnAmount: 'Amount',
    columnStatus: 'Status',
    columnRequested: 'Raised',
    columnAction: 'Action',
    raise: 'Raise a refund',
    ofPayment: 'of',
    empty: 'Nothing is waiting.',
  };

  it('offers the raise link to a caller who holds Refunds · Write', () => {
    render(<RefundQueueTable rows={rows} canRaise labels={LABELS} />);

    expect(screen.getByRole('link', { name: LABELS.raise }).getAttribute('href')).toContain(
      PAYMENT,
    );
  });

  it('draws no action column at all for a caller who does not', () => {
    // URD §2.3's `◐ raise/recommend` for the Support CSR: the queue is theirs and
    // the execution is not, so the control is absent rather than disabled.
    render(<RefundQueueTable rows={rows} canRaise={false} labels={LABELS} />);

    expect(screen.queryByRole('link', { name: LABELS.raise })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: LABELS.columnAction })).toBeNull();
  });
});

describe('the payouts panel', () => {
  it('shows a failure and offers no retry, because there is nothing to re-send', () => {
    render(
      <PayoutsPanel
        batches={[]}
        payouts={[
          {
            key: 'p1',
            driverId: DRIVER,
            amount: 'Rs 15,000.00',
            status: { tone: 'error', label: 'FAILED' },
            account: '4321',
            failureReason: 'Account closed',
            created: '17 Jun 09:12',
            settled: null,
          },
        ]}
        labels={{
          heading: 'Payout instructions',
          note: 'The weekly sweep pays each driver their whole balance.',
          batchesHeading: 'Weekly runs',
          batchesCaption: 'Payout runs',
          batchRun: 'Run date',
          batchStatus: 'Status',
          batchInstructions: 'Instructions',
          batchTotal: 'Total paid out',
          batchCompleted: 'Completed',
          batchesEmpty: 'No payout run yet.',
          instructionsCaption: 'Payout instructions',
          columnDriver: 'Driver',
          columnAmount: 'Amount',
          columnStatus: 'Status',
          columnAccount: 'Account',
          columnCreated: 'Created',
          columnSettled: 'Settled',
          instructionsEmpty: 'No payout instruction yet.',
          noRetry: 'A failed payout has already been put back on the driver’s wallet',
        }}
      />,
    );

    expect(screen.getByText('Account closed')).toBeDefined();
    expect(screen.queryByRole('button', { name: /retry/i })).toBeNull();
    expect(screen.getByText(/already been put back/)).toBeDefined();
  });
});

describe('the tab strip', () => {
  it('marks the current tab as the page and the others as plain links', () => {
    render(
      <ScreenTabs
        navLabel="Finance views"
        tabs={[
          { id: 'settlement', href: '/finance/reconciliation', label: 'Gateway settlement', current: true },
          { id: 'ledger', href: '/finance/transactions', label: 'Wallet ledger', current: false },
        ]}
      />,
    );

    const nav = screen.getByRole('navigation', { name: 'Finance views' });
    const current = within(nav)
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.getAttribute('href')).toBe('/finance/reconciliation');
  });

  it('draws nothing at all when the caller’s menu carries none of the screens', () => {
    const { container } = render(<ScreenTabs navLabel="Finance views" tabs={[]} />);
    expect(container.firstChild).toBeNull();
  });
});
