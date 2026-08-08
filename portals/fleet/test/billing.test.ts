import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  DASHBOARD_INVOICE_LIMIT,
  FLEET_BILLING,
  FLEET_WALLET,
  invoiceStatusView,
  nextPayableInvoice,
  type FleetInvoice,
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

const contract = readFileSync(BILLING_CONTRACT, 'utf8');
const access = readFileSync(BILLING_ACCESS, 'utf8');

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
