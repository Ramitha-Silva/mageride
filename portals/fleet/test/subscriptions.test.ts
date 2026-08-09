import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  acceptRequestTarget,
  byActiveFirst,
  byNewestPeriod,
  canConfirm,
  canDelete,
  canMarkCash,
  canSetFare,
  confirmPaymentTarget,
  paymentsFileName,
  pendingConfirmation,
  pendingRequests,
  rejectRequestTarget,
  subscriberFareTarget,
  subscriberMarkCashTarget,
  subscriberPaymentsTarget,
  subscriberTarget,
  subscriptionTotals,
  thisMonthView,
  vehicleRequestsTarget,
  vehicleSubscribersTarget,
  CASH_IS_NOT_KNOWN_IN_ADVANCE,
  NEXT_DUE_DATE_UNAVAILABLE,
  REJECT_REASON_MAX_LENGTH,
  SUBSCRIPTION_MONEY_IS_PASS_THROUGH,
  SUBSCRIPTION_PAGE_LIMIT,
  type SubscriberRow,
  type SubscriptionPaymentRow,
} from '@/api/subscriptions';
import { subscribableVehicles } from '@/components/subscriptions/subscription-model';
import { createFleetTranslator } from '@/i18n';

import { contractEnum, FLEET_CONTRACT, FLEET_OPS_ENDPOINTS_SOURCE } from './support/fleet';

/**
 * **SCR-FP-011 and SCR-FP-012 against the contracts they are transcribed from.**
 *
 * Five things this file exists to hold, none of which any other test can:
 *
 *  1. every target this component builds is a path `fleet.yaml` actually
 *     declares — a proxy renamed upstream is a failing test, not a 404 an
 *     operator finds;
 *  2. AL-23's fence: **every** Epic 23 route except the slip confirmation is
 *     addressed by vehicle, so a fleet-wide roster cannot be introduced by
 *     accident;
 *  3. the Owner/Manager split is `FleetOpsEndpoints`' own, parsed out of the C#
 *     rather than remembered;
 *  4. AL-59's `onepay` removal, which fleet.yaml's copy of the enum has **not**
 *     followed — the portal renders the wider union on purpose and this is where
 *     the drift is recorded;
 *  5. the three gaps the screens state in words, each asserted against the
 *     contract that creates it.
 */

const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const CONTRACTS = dirname(FLEET_CONTRACT);
const fleet = readFileSync(FLEET_CONTRACT, 'utf8');
const subscription = readFileSync(join(CONTRACTS, 'subscription.yaml'), 'utf8');
const ops = readFileSync(FLEET_OPS_ENDPOINTS_SOURCE, 'utf8');

const VEHICLE = '01JQV000000000000000000002';
const SUBSCRIBER = '01JQS000000000000000000001';
const REQUEST = '01JQR000000000000000000001';
const PAYMENT = '01JQP000000000000000000001';

const t = createFleetTranslator('en');

/** An org-relative target as the URL `src/api/client.ts` would build from it. */
function url(target: string): string {
  return `/v1/fleets/{fleetId}${target}`;
}

/** The `{param}` spelling `fleet.yaml` declares, from a target with real ids in it. */
function template(target: string): string {
  return url(target)
    .replace(VEHICLE, '{vehicleId}')
    .replace(SUBSCRIBER, '{subscriberId}')
    .replace(REQUEST, '{requestId}')
    .replace(PAYMENT, '{paymentId}');
}

describe('every target is a path fleet.yaml declares', () => {
  const targets = [
    vehicleRequestsTarget(VEHICLE),
    acceptRequestTarget(VEHICLE, REQUEST),
    rejectRequestTarget(VEHICLE, REQUEST),
    vehicleSubscribersTarget(VEHICLE),
    subscriberTarget(VEHICLE, SUBSCRIBER),
    subscriberFareTarget(VEHICLE, SUBSCRIBER),
    subscriberMarkCashTarget(VEHICLE, SUBSCRIBER),
    subscriberPaymentsTarget(VEHICLE, SUBSCRIBER),
    confirmPaymentTarget(PAYMENT),
  ];

  it.each(targets)('declares %s', (target) => {
    expect(fleet).toContain(`  ${template(target)}:`);
  });

  it('names the eight operations the two screens call', () => {
    for (const operationId of [
      'listFleetVehicleRequests',
      'acceptFleetVehicleRequest',
      'rejectFleetVehicleRequest',
      'listFleetVehicleSubscribers',
      'deleteFleetVehicleSubscriber',
      'setFleetSubscriberFare',
      'markFleetSubscriberCashPaid',
      'listFleetSubscriberPayments',
      'confirmFleetTransferSlip',
    ]) {
      expect(fleet, operationId).toContain(`operationId: ${operationId}`);
    }
  });
});

describe('AL-23 — sharing and requests are scoped per vehicle', () => {
  it('addresses every roster and queue route by vehicle', () => {
    for (const target of [
      vehicleRequestsTarget(VEHICLE),
      acceptRequestTarget(VEHICLE, REQUEST),
      rejectRequestTarget(VEHICLE, REQUEST),
      vehicleSubscribersTarget(VEHICLE),
      subscriberTarget(VEHICLE, SUBSCRIBER),
      subscriberFareTarget(VEHICLE, SUBSCRIBER),
      subscriberMarkCashTarget(VEHICLE, SUBSCRIBER),
      subscriberPaymentsTarget(VEHICLE, SUBSCRIBER),
    ]) {
      expect(target, `${target} is not addressed by vehicle`).toContain(`/vehicles/${VEHICLE}/`);
    }
  });

  it('leaves exactly one route addressed by payment instead', () => {
    // `confirmFleetTransferSlip` hangs off the fleet group, and subscription-svc
    // resolves the payment's own vehicle and checks ownership against it — so the
    // fence is held on the far side rather than in this URL. That is the only
    // exception, and it is one the contract makes rather than one this portal
    // takes.
    expect(confirmPaymentTarget(PAYMENT)).toBe(`/payments/${PAYMENT}/confirm`);
    expect(confirmPaymentTarget(PAYMENT)).not.toContain('/vehicles/');
  });

  it('offers only Mode B vehicles to scope a screen to', () => {
    const choices = subscribableVehicles(
      [
        { vehicleId: 'a', registrationNumber: 'NB-4521', vehicleType: 'bus', mode: 'A', status: 'APPROVED' },
        {
          vehicleId: 'b',
          registrationNumber: 'VN-8810',
          vehicleType: 'van',
          mode: 'B',
          status: 'APPROVED',
          modeBBilling: 'paid',
        },
        {
          vehicleId: 'c',
          registrationNumber: 'VN-9911',
          vehicleType: 'van',
          mode: 'B',
          status: 'APPROVED',
          modeBBilling: 'free',
        },
      ],
      t,
    );

    expect(choices.map((choice) => choice.vehicleId)).toEqual(['b', 'c']);
    expect(choices[0]!.paid).toBe(true);
    // A Free Mode B vehicle still has a queue and a roster — only the fare and
    // payment columns fall away.
    expect(choices[1]!.paid).toBe(false);
  });
});

describe('the Owner/Manager split is FleetOpsEndpoints’ own', () => {
  /** The `RequireFleetSubRole(FleetRoles.X)` that follows a `WithName("op")`. */
  function subRoleOf(operationId: string): string {
    const match = new RegExp(
      `WithName\\("${operationId}"\\)\\s*\\n\\s*\\.RequireFleetSubRole\\(FleetRoles\\.(\\w+)\\)`,
    ).exec(ops);
    if (!match) throw new Error(`No RequireFleetSubRole on ${operationId}.`);
    return match[1]!;
  }

  it('gives the queue and the roster to a Manager', () => {
    for (const operationId of [
      'listFleetVehicleRequests',
      'acceptFleetVehicleRequest',
      'rejectFleetVehicleRequest',
      'listFleetVehicleSubscribers',
    ]) {
      expect(subRoleOf(operationId), operationId).toBe('Manager');
    }
  });

  it('keeps the money and the delete for the Owner', () => {
    for (const operationId of [
      'deleteFleetVehicleSubscriber',
      'setFleetSubscriberFare',
      'markFleetSubscriberCashPaid',
      'listFleetSubscriberPayments',
      'confirmFleetTransferSlip',
    ]) {
      expect(subRoleOf(operationId), operationId).toBe('Owner');
    }
  });

  it('carries the approval gate on both proxy groups', () => {
    // `canMutate(..., { requiresApprovedOrg: true })` on every write in
    // `subscription-actions.ts` is this, transcribed.
    const block = ops.slice(ops.indexOf('MapSubscriptionProxies'));
    expect(block.match(/RequireApprovedFleet\(\)/g)?.length ?? 0).toBeGreaterThanOrEqual(2);
  });
});

describe('AL-59 — onepay is gone from Mode B, and fleet.yaml has not caught up', () => {
  it('has four methods on subscription-svc’s own enum', () => {
    expect(contractEnum(subscription, 'lankaqr_deeplink')).toEqual([
      'lankaqr_deeplink',
      'lankaqr_scan',
      'online_transfer',
      'cash',
    ]);
  });

  it('still has five on the fleet-svc proxy’s copy', () => {
    // The union in `@/api/subscriptions` is the wider of the two on purpose: a
    // payment row written before AL-59 renders as a historic method rather than
    // as a blank cell. Nothing on this portal *offers* a method — a subscription
    // payment is initiated by the passenger — so the drift costs a label.
    //
    // When fleet.yaml is corrected this test fails, and the union loses a member
    // in the same change.
    expect(contractEnum(fleet, 'lankaqr_deeplink')).toEqual([
      'lankaqr_deeplink',
      'lankaqr_scan',
      'onepay',
      'online_transfer',
      'cash',
    ]);
  });

  it('offers no payment method anywhere on this portal', () => {
    // The owner's only write on a payment is the cash mark and the slip confirm,
    // and neither takes a method. A `<select name="method">` on these screens
    // would be this console initiating a payment the passenger owns.
    const forms = readFileSync(
      join(APP_ROOT, 'src/components/subscriptions/MarkCashForm.tsx'),
      'utf8',
    );
    expect(forms).not.toContain('name="method"');
  });
});

describe('the three enums the roster renders', () => {
  it('matches the wire’s cycles, month statuses and grant statuses', () => {
    expect(contractEnum(fleet, 'month_first')).toEqual(['month_first', 'join_anniversary']);
    expect(contractEnum(fleet, 'paid, unpaid')).toEqual(['paid', 'unpaid', 'pending_verification']);
    expect(contractEnum(fleet, 'active, unsubscribed')).toEqual(['active', 'unsubscribed']);
    expect(contractEnum(fleet, 'initiated')).toEqual([
      'initiated',
      'pending_verification',
      'paid',
      'failed',
    ]);
  });

  it('asks for a page no larger than the shared limit admits', () => {
    const limit = /Limit:[\s\S]*?maximum: (\d+)/.exec(
      readFileSync(join(CONTRACTS, '_shared.yaml'), 'utf8'),
    );
    expect(Number(limit?.[1])).toBe(SUBSCRIPTION_PAGE_LIMIT);
  });

  it('caps a rejection reason where the contract does', () => {
    expect(fleet).toContain(`maxLength: ${REJECT_REASON_MAX_LENGTH}`);
  });
});

describe('the gaps the screens state in words', () => {
  it('has no next-due date on the fleet roster row, and the passenger’s card does', () => {
    // US-23.8 asks for the cycle **and the next-due date** on both sides.
    // `SubscriberRow` carries neither `nextDue` nor `joinDay`; `Subscription`,
    // which is the passenger's own card, carries both. So the fleet column names
    // the cycle and says where the date is — `NEXT_DUE_DATE_UNAVAILABLE`.
    const row = fleet.slice(fleet.indexOf('    SubscriberRow:'));
    const rowBlock = row.slice(0, row.indexOf('\n    SubscriptionPaymentRow:'));
    expect(rowBlock).not.toContain('nextDue');
    expect(rowBlock).not.toContain('joinDay');

    const card = subscription.slice(subscription.indexOf('    Subscription:'));
    expect(card.slice(0, card.indexOf('\n    SubscriberRow:'))).toContain('nextDue');

    expect(NEXT_DUE_DATE_UNAVAILABLE).toBe(true);
  });

  it('has no payment id on the roster row, which is why Confirm needs the ledger', () => {
    const row = fleet.slice(fleet.indexOf('    SubscriberRow:'));
    expect(row.slice(0, row.indexOf('\n    SubscriptionPaymentRow:'))).not.toContain('paymentId');
  });

  it('has no export route, which is why the CSV is written in this repo', () => {
    expect(fleet).not.toMatch(/subscribers\/\{subscriberId\}\/payments\.csv/);
    expect(fleet).not.toContain('exportFleetSubscriberPayments');
  });

  it('states that an unpaid month is not yet a cash month', () => {
    expect(CASH_IS_NOT_KNOWN_IN_ADVANCE).toBe(true);
  });

  it('states that this money is the owner’s', () => {
    expect(SUBSCRIPTION_MONEY_IS_PASS_THROUGH).toBe(true);
  });
});

/* ---------------------------------------------------------------------------
 * The predicates the screens draw their controls from
 * ------------------------------------------------------------------------ */

function subscriber(overrides: Partial<SubscriberRow> = {}): SubscriberRow {
  return {
    subscriberId: SUBSCRIBER,
    passengerId: '01JQX000000000000000000001',
    name: 'Ramith de Silva',
    mobileMasked: '+94 77 *** 4567',
    billing: 'paid',
    monthlyFareMinor: 600_000,
    currency: 'LKR',
    cycle: 'join_anniversary',
    thisMonthStatus: 'unpaid',
    muted: false,
    status: 'active',
    ...overrides,
  };
}

function payment(overrides: Partial<SubscriptionPaymentRow> = {}): SubscriptionPaymentRow {
  return {
    paymentId: PAYMENT,
    subscriptionId: '01JQU000000000000000000001',
    method: 'online_transfer',
    amountMinor: 600_000,
    currency: 'LKR',
    status: 'pending_verification',
    periodMonth: '2026-06-01',
    ...overrides,
  };
}

describe('a control is drawn only where the service would accept it', () => {
  it('sets a fare on a live Paid subscription and nowhere else', () => {
    expect(canSetFare(subscriber())).toBe(true);
    // `ck_subscriptions_fare` refuses a fare on a Free subscription (BR-23.8).
    expect(canSetFare(subscriber({ billing: 'free', monthlyFareMinor: undefined }))).toBe(false);
    // An ended subscription has nothing to bill.
    expect(canSetFare(subscriber({ status: 'unsubscribed', muted: true }))).toBe(false);
  });

  it('marks cash on a collectable month that is not already paid', () => {
    expect(canMarkCash(subscriber())).toBe(true);
    expect(canMarkCash(subscriber({ thisMonthStatus: 'pending_verification' }))).toBe(true);
    expect(canMarkCash(subscriber({ thisMonthStatus: 'paid' }))).toBe(false);
    expect(canMarkCash(subscriber({ billing: 'free' }))).toBe(false);
  });

  it('deletes a muted row and refuses an active one (AL-25)', () => {
    expect(canDelete(subscriber())).toBe(false);
    expect(canDelete(subscriber({ muted: true }))).toBe(true);
    expect(canDelete(subscriber({ status: 'unsubscribed' }))).toBe(true);
  });

  it('confirms a slip that is awaiting verification and nothing else', () => {
    expect(canConfirm(payment())).toBe(true);
    expect(canConfirm(payment({ status: 'paid' }))).toBe(false);
    expect(canConfirm(payment({ status: 'initiated' }))).toBe(false);
    expect(canConfirm(payment({ method: 'cash', status: 'paid' }))).toBe(false);
  });

  it('finds the month a Confirm is about, or nothing', () => {
    expect(pendingConfirmation([payment({ status: 'paid' }), payment()])?.paymentId).toBe(PAYMENT);
    expect(pendingConfirmation([payment({ status: 'paid' })])).toBeNull();
  });
});

describe('a muted row is rendered, never filtered out (US-23.12)', () => {
  it('gives it the neutral chip whatever its month said', () => {
    const view = thisMonthView(subscriber({ muted: true, thisMonthStatus: 'unpaid' }));
    expect(view.tone).toBe('neutral');
    expect(view.labelKey).toBe('fleet.subscriptions.status.unsubscribed');
  });

  it('sorts it below the active rows and keeps it in the list', () => {
    const rows = [subscriber({ subscriberId: 'muted', muted: true }), subscriber()];
    const sorted = [...rows].sort(byActiveFirst);

    expect(sorted).toHaveLength(2);
    expect(sorted[1]!.subscriberId).toBe('muted');
  });

  it('carries no fare into the totals — a free service has none to owe', () => {
    const totals = subscriptionTotals([
      subscriber({ subscriberId: '1', thisMonthStatus: 'paid' }),
      subscriber({ subscriberId: '2', thisMonthStatus: 'pending_verification' }),
      subscriber({ subscriberId: '3', thisMonthStatus: 'unpaid', monthlyFareMinor: 550_000 }),
      subscriber({ subscriberId: '4', billing: 'free', monthlyFareMinor: undefined }),
      subscriber({ subscriberId: '5', muted: true, status: 'unsubscribed' }),
    ]);

    expect(totals).toEqual({
      paidMinor: 600_000,
      paidCount: 1,
      pendingMinor: 600_000,
      pendingCount: 1,
      dueMinor: 550_000,
      dueCount: 1,
      activeCount: 4,
      mutedCount: 1,
      freeCount: 1,
    });
  });
});

describe('the queue and the ledger are ordered the way they are read', () => {
  it('shows pending requests only', () => {
    const rows = pendingRequests([
      { requestId: '1', vehicleId: VEHICLE, passengerId: 'p', status: 'pending', createdAt: 'x' },
      { requestId: '2', vehicleId: VEHICLE, passengerId: 'p', status: 'accepted', createdAt: 'x' },
      { requestId: '3', vehicleId: VEHICLE, passengerId: 'p', status: 'rejected', createdAt: 'x' },
    ]);

    expect(rows.map((row) => row.requestId)).toEqual(['1']);
  });

  it('puts the newest month at the top of the ledger', () => {
    const rows = [
      payment({ paymentId: 'apr', periodMonth: '2026-04-01' }),
      payment({ paymentId: 'jun', periodMonth: '2026-06-01' }),
      payment({ paymentId: 'may', periodMonth: '2026-05-01' }),
    ].sort(byNewestPeriod);

    expect(rows.map((row) => row.paymentId)).toEqual(['jun', 'may', 'apr']);
  });
});

describe('the export names itself after the vehicle and the grant', () => {
  it('slugs a plate and keeps the id verbatim', () => {
    expect(paymentsFileName('VN-8810', SUBSCRIBER)).toBe(
      `mageride-subscriber-payments-vn-8810-${SUBSCRIBER}.csv`,
    );
  });

  it('survives a plate with nothing sluggable in it', () => {
    expect(paymentsFileName('   ', SUBSCRIBER)).toBe(
      `mageride-subscriber-payments-vehicle-${SUBSCRIBER}.csv`,
    );
  });
});
