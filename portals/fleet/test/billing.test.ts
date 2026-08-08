import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  hasReceipt,
  invoiceExportTarget,
  invoicePayTarget,
  invoiceReceiptTarget,
  invoiceStatusView,
  invoiceSummary,
  invoiceTarget,
  isInvoiceExportFormat,
  isPayable,
  isTopupMethod,
  nextPayableInvoice,
  topupTarget,
  BILLING_PAGE_LIMIT,
  DASHBOARD_INVOICE_LIMIT,
  FLEET_BILLING,
  FLEET_WALLET,
  FLEET_WALLET_TOPUP,
  INVOICE_EXPORT_FORMATS,
  MAX_TOPUP_MINOR,
  MIN_TOPUP_MINOR,
  TOPUP_METHODS,
  TOPUP_PENDING_WINDOW_SECONDS,
  WALLET_MOVEMENT_LIMIT,
  type FleetInvoice,
  type FleetInvoiceDetail,
  type FleetInvoiceLine,
} from '@/api/billing';
import { billingRefusal, canReadBilling } from '@/server/access';

import { sessionFor, sessionWithoutOrganisation } from './support/fleet';

/**
 * **The dashboard's wallet card**, against the service that gates it and the
 * contract that shapes it.
 *
 * The one thing worth asserting hardest is the gate. `fleet-billing.yaml` is the
 * only surface on this portal where a **read** is Owner-only and approval-gated,
 * which is stricter than fleet-svc and stricter than URD §2.3 read on its own — so
 * "a Manager sees a sentence, not a 403" is a property that has to be checked
 * against `FleetBillingAccessFilter` rather than remembered.
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const BILLING_CONTRACT = join(REPO_ROOT, 'backend/contracts/fleet-billing.yaml');
const BILLING_ACCESS = join(
  REPO_ROOT,
  'backend/src/FleetBilling/Authorization/FleetBillingAccess.cs',
);
const BILLING_OPTIONS = join(
  REPO_ROOT,
  'backend/src/FleetBilling/Configuration/FleetBillingOptions.cs',
);
const SHARED_CONTRACT = join(REPO_ROOT, 'backend/contracts/_shared.yaml');
const TOPUP_MIGRATION = join(REPO_ROOT, 'db/migrations/1108__billing_fleet_billing.sql');

const contract = readFileSync(BILLING_CONTRACT, 'utf8');
const access = readFileSync(BILLING_ACCESS, 'utf8');
const options = readFileSync(BILLING_OPTIONS, 'utf8');
const shared = readFileSync(SHARED_CONTRACT, 'utf8');

function invoice(overrides: Partial<FleetInvoice>): FleetInvoice {
  return {
    invoiceId: '01JQI000000000000000000001',
    periodMonth: '2026-06-01',
    amountMinor: 6_900_000,
    currency: 'LKR',
    status: 'DUE',
    ...overrides,
  };
}

describe('the two targets are fleet-billing-svc’s', () => {
  it('names paths the contract declares under the org prefix', () => {
    expect(contract).toContain(`/v1/fleets/{fleetId}${FLEET_WALLET}:`);
    expect(contract).toContain(`/v1/fleets/{fleetId}${FLEET_BILLING}:`);
  });

  it('is org-relative, so the data layer is the only thing that names an organisation', () => {
    for (const target of [FLEET_WALLET, FLEET_BILLING]) {
      expect(target.startsWith('/')).toBe(true);
      expect(target).not.toContain('/v1/');
    }
  });

  it('carries every required field of FleetWallet', () => {
    const required = /FleetWallet:\s*\n\s*type: object\s*\n\s*required: \[([^\]]*)\]/
      .exec(contract)?.[1]
      ?.split(',')
      .map((field) => field.trim());

    expect(required).toEqual([
      'balanceMinor',
      'outstandingMinor',
      'availableMinor',
      'currency',
      'movements',
    ]);
  });

  it('mirrors the four invoice states', () => {
    expect(contract).toContain('enum: [FREE, DUE, PAID, OVERDUE]');
    for (const status of ['FREE', 'DUE', 'PAID', 'OVERDUE'] as const) {
      expect(invoiceStatusView(status).labelKey.startsWith('fleet.billing.status.')).toBe(true);
    }
    expect(invoiceStatusView('OVERDUE').tone).toBe('error');
    expect(invoiceStatusView('DUE').tone).toBe('warning');
    expect(invoiceStatusView('PAID').tone).toBe('success');
    // A zero invoice is not a paid one — it is a month with nothing billable.
    expect(invoiceStatusView('FREE').tone).toBe('neutral');
  });
});

describe('the gate is FleetBillingAccessFilter’s, transcribed', () => {
  it('is Owner-only on reads, which is what the service says in the C#', () => {
    expect(access).toContain('FleetRoles.Satisfies(fleetRole, MageRide.Shared.Auth.FleetRoles.Owner)');
    expect(access).toContain('FleetRoleInsufficient');

    expect(canReadBilling(sessionFor('owner'))).toBe(true);
    expect(canReadBilling(sessionFor('manager'))).toBe(false);
    expect(canReadBilling(sessionFor('viewer'))).toBe(false);
  });

  it('is approval-gated, which fleet-svc’s map and analytics deliberately are not', () => {
    expect(access).toContain('FleetNotApproved');

    expect(canReadBilling(sessionFor('owner', 'PENDING'))).toBe(false);
    expect(canReadBilling(sessionFor('owner', 'REJECTED'))).toBe(false);
  });

  it('refuses an account with no organisation at all', () => {
    expect(canReadBilling(sessionWithoutOrganisation())).toBe(false);
  });

  it('separates the two refusals, because an operator does two things about them', () => {
    expect(billingRefusal(sessionFor('owner'))).toBe(null);
    expect(billingRefusal(sessionFor('owner', 'PENDING'))).toBe('pending-org');
    expect(billingRefusal(sessionFor('manager'))).toBe('not-owner');
    expect(billingRefusal(sessionFor('viewer', 'PENDING'))).toBe('not-owner');
  });
});

describe('the next consolidated invoice', () => {
  it('is nothing when every month is settled or free', () => {
    expect(
      nextPayableInvoice([
        invoice({ status: 'PAID', periodMonth: '2026-06-01' }),
        invoice({ status: 'FREE', periodMonth: '2026-05-01', amountMinor: 0 }),
      ]),
    ).toBe(null);

    expect(nextPayableInvoice([])).toBe(null);
    expect(nextPayableInvoice(undefined)).toBe(null);
  });

  it('takes the overdue month ahead of the one just raised', () => {
    // `GET …/billing` answers newest month first, which is the wrong end for this
    // question: dunning is chasing the older one, and taking the response's first
    // row would name the month just raised and leave the overdue one off screen.
    const answer = nextPayableInvoice([
      invoice({ status: 'DUE', periodMonth: '2026-06-01' }),
      invoice({ status: 'OVERDUE', periodMonth: '2026-05-01' }),
    ]);

    expect(answer?.periodMonth).toBe('2026-05-01');
    expect(answer?.status).toBe('OVERDUE');
  });

  it('takes the oldest open month when both are the same state', () => {
    const answer = nextPayableInvoice([
      invoice({ status: 'DUE', periodMonth: '2026-06-01' }),
      invoice({ status: 'DUE', periodMonth: '2026-05-01' }),
    ]);

    expect(answer?.periodMonth).toBe('2026-05-01');
  });

  it('reads one small page, since the card names one invoice', () => {
    expect(DASHBOARD_INVOICE_LIMIT).toBeGreaterThan(0);
    expect(DASHBOARD_INVOICE_LIMIT).toBeLessThanOrEqual(50);
  });
});

/* ---------------------------------------------------------------------------
 * Δ C115 — SCR-FP-010
 * ------------------------------------------------------------------------ */

function line(overrides: Partial<FleetInvoiceLine> = {}): FleetInvoiceLine {
  return {
    vehicleId: '01JQV000000000000000000001',
    registrationNumber: 'NB-4521',
    vehicleType: 'bus',
    amountMinor: 30_000,
    currency: 'LKR',
    status: 'DUE',
    ...overrides,
  };
}

function detail(lines: FleetInvoiceLine[], overrides: Partial<FleetInvoice> = {}): FleetInvoiceDetail {
  const lineSumMinor = lines.reduce((sum, row) => sum + row.amountMinor, 0);
  return {
    invoice: invoice({ amountMinor: lineSumMinor, vehicleCount: lines.length, ...overrides }),
    lines,
    lineSumMinor,
  };
}

describe('the six routes SCR-FP-010 adds are the ones the contract declares', () => {
  it('names every path under the org prefix, and none absolutely', () => {
    const id = '01JQI000000000000000000001';

    const targets: Readonly<Record<string, string>> = {
      [invoiceTarget(id)]: '/v1/fleets/{fleetId}/billing/{invoiceId}:',
      [invoiceExportTarget(id)]: '/v1/fleets/{fleetId}/billing/{invoiceId}/export:',
      [invoiceReceiptTarget(id)]: '/v1/fleets/{fleetId}/billing/{invoiceId}/receipt:',
      [invoicePayTarget(id)]: '/v1/fleets/{fleetId}/billing/{invoiceId}/pay:',
      [FLEET_WALLET_TOPUP]: '/v1/fleets/{fleetId}/wallet/topup:',
      [topupTarget(id)]: '/v1/fleets/{fleetId}/wallet/topup/{topupId}:',
    };

    for (const [target, path] of Object.entries(targets)) {
      expect(contract, path).toContain(path);
      expect(target.startsWith('/'), target).toBe(true);
      expect(target, target).not.toContain('/v1/');
    }
  });

  it('carries every required field of the invoice detail and the line', () => {
    const required = (schema: string) =>
      new RegExp(`${schema}:\\s*\\n[\\s\\S]{0,200}?required: \\[([^\\]]*)\\]`)
        .exec(contract)?.[1]
        ?.split(',')
        .map((field) => field.trim());

    expect(required('FleetInvoiceDetail')).toEqual(['invoice', 'lines', 'lineSumMinor']);
    expect(required('FleetInvoiceLine')).toEqual([
      'vehicleId',
      'registrationNumber',
      'vehicleType',
      'amountMinor',
      'currency',
      'status',
    ]);
  });

  it('serves both export formats, so the Download control offers both', () => {
    expect(contract).toContain('enum: [csv, pdf]');
    expect([...INVOICE_EXPORT_FORMATS]).toEqual(['csv', 'pdf']);
    expect(isInvoiceExportFormat('csv')).toBe(true);
    expect(isInvoiceExportFormat('xlsx')).toBe(false);
  });

  it('keeps both page limits inside FleetBilling:MaxPageSize', () => {
    const maxPageSize = /MaxPageSize \{ get; set; \} = (\d+)/.exec(options);
    expect(maxPageSize, 'MaxPageSize could not be read').not.toBeNull();

    expect(BILLING_PAGE_LIMIT).toBeLessThanOrEqual(Number(maxPageSize![1]));
    expect(WALLET_MOVEMENT_LIMIT).toBeLessThanOrEqual(Number(maxPageSize![1]));
  });
});

describe('AL-05 — two rails, and bank transfer is not one of them', () => {
  it('offers exactly the methods the contract admits', () => {
    expect(contract).toContain('enum: [onepay, lankaqr]');
    expect([...TOPUP_METHODS]).toEqual(['onepay', 'lankaqr']);

    expect(isTopupMethod('onepay')).toBe(true);
    expect(isTopupMethod('lankaqr')).toBe(true);
    // The two an AL-05 violation would be spelled as.
    expect(isTopupMethod('bank_transfer')).toBe(false);
    expect(isTopupMethod('transfer')).toBe(false);
  });

  it('is refused by the database as well, which is where the fence actually is', () => {
    const migration = readFileSync(TOPUP_MIGRATION, 'utf8');
    expect(migration).toContain('ck_fleet_topups_method');
    expect(migration).toMatch(/ck_fleet_topups_method[\s\S]{0,200}?'onepay'\s*,\s*'lankaqr'/);
  });

  it('keeps the card rail as OnePay rather than as a third method', () => {
    // The wireframe draws Card, OnePay and LankaQR; the platform has two rails,
    // and OnePay is the card one.
    expect(options).toContain('the card rail answers 503');
  });

  it('states the amount bounds the service enforces', () => {
    expect(options).toMatch(
      new RegExp(`MinTopupMinor \\{ get; set; \\} = ${MIN_TOPUP_MINOR.toLocaleString('en-US').replaceAll(',', '_')}`),
    );
    expect(options).toMatch(
      new RegExp(`MaxTopupMinor \\{ get; set; \\} = ${MAX_TOPUP_MINOR.toLocaleString('en-US').replaceAll(',', '_')}`),
    );
    expect(options).toMatch(
      new RegExp(`TopupPendingWindow \\{ get; set; \\} = TimeSpan\\.FromSeconds\\(${TOPUP_PENDING_WINDOW_SECONDS}\\)`),
    );
  });
});

describe('an invoice’s lines sum to its total and exclude Mode A vehicles', () => {
  it('totals Σ of the lines and nothing else', () => {
    const answer = invoiceSummary(detail([line(), line({ vehicleId: 'v2' })]), 88);

    expect(answer.totalMinor).toBe(60_000);
    expect(answer.reconciles).toBe(true);
  });

  it('draws the Mode A row from the roster, worth nothing, outside the total', () => {
    const answer = invoiceSummary(detail([line()]), 88);
    const modeA = answer.rows.find((row) => row.key === 'mode-a');

    expect(modeA?.qty).toBe(88);
    expect(modeA?.amountMinor).toBe(0);
    // 88 free vehicles change the total by nothing at all.
    expect(answer.totalMinor).toBe(30_000);

    // And no Mode A line exists to be excluded: the contract has no `mode` on a
    // line, because a line can only exist for a Mode B charge.
    const schema = /FleetInvoiceLine:[\s\S]*?FleetInvoiceDetail:/.exec(contract)?.[0] ?? '';
    expect(schema).not.toMatch(/^\s+mode:/m);
  });

  it('separates a vehicle’s free first month from a charged one (D5’ §2.1)', () => {
    const answer = invoiceSummary(
      detail([line(), line({ vehicleId: 'v2', status: 'FREE', amountMinor: 0 })]),
      0,
    );

    expect(answer.rows.find((row) => row.key === 'mode-b')?.qty).toBe(1);
    expect(answer.rows.find((row) => row.key === 'mode-b-free')?.qty).toBe(1);
    expect(answer.totalMinor).toBe(30_000);
    expect(answer.reconciles).toBe(true);
  });

  it('draws no free row on a month that has none', () => {
    const answer = invoiceSummary(detail([line()]), 0);
    expect(answer.rows.some((row) => row.key === 'mode-b-free')).toBe(false);
  });

  it('says the rate varies rather than printing one of two', () => {
    const answer = invoiceSummary(detail([line(), line({ vehicleId: 'v2', amountMinor: 25_000 })]), 0);
    const modeB = answer.rows.find((row) => row.key === 'mode-b');

    expect(modeB?.mixedRate).toBe(true);
    expect(modeB?.rateMinor).toBeNull();
    expect(answer.totalMinor).toBe(55_000);
  });

  it('checks lineSumMinor rather than trusting it', () => {
    const wrong: FleetInvoiceDetail = { ...detail([line()]), lineSumMinor: 1 };
    expect(invoiceSummary(wrong, 0).reconciles).toBe(false);

    const disagrees: FleetInvoiceDetail = {
      ...detail([line()]),
      invoice: invoice({ amountMinor: 999 }),
    };
    expect(invoiceSummary(disagrees, 0).reconciles).toBe(false);
  });

  it('renders a Mode-A-only organisation’s FREE invoice as zero lines and zero total', () => {
    const answer = invoiceSummary(
      detail([], { status: 'FREE', amountMinor: 0, vehicleCount: 0 }),
      88,
    );

    expect(answer.totalMinor).toBe(0);
    expect(answer.reconciles).toBe(true);
    expect(answer.rows.find((row) => row.key === 'mode-b')?.qty).toBe(0);
  });
});

describe('the Pay button and the receipt are drawn for the states that have them', () => {
  it('is drawn for an open month and for neither of the two 409s', () => {
    expect(isPayable(invoice({ status: 'DUE' }))).toBe(true);
    expect(isPayable(invoice({ status: 'OVERDUE' }))).toBe(true);
    // "A FREE invoice has a zero total and no journal entry could balance."
    expect(isPayable(invoice({ status: 'FREE', amountMinor: 0 }))).toBe(false);
    expect(isPayable(invoice({ status: 'PAID' }))).toBe(false);

    expect(shared).toContain('invoice-not-payable');
    expect(shared).toContain('insufficient-wallet');
  });

  it('reads a receipt only for a settled invoice, which is the only one that has one', () => {
    expect(hasReceipt(invoice({ status: 'PAID', journalEntryId: 'j1' }))).toBe(true);
    // `ck_fleet_invoices_posting` makes the pair a database fact; a PAID invoice
    // with no entry is not a state the platform can be in, and asking for its
    // receipt would be a 404 on every render.
    expect(hasReceipt(invoice({ status: 'PAID' }))).toBe(false);
    expect(hasReceipt(invoice({ status: 'DUE' }))).toBe(false);
  });
});
