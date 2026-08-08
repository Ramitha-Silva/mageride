import { beforeEach, describe, expect, it, vi } from 'vitest';

import { isAuditIntent } from '@/api/audit';
import type { MutateOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-006's two money decisions, as the requests they become (E-05, US-14.11,
 * D-35).
 *
 * Four properties, each one a place where the plausible implementation is wrong:
 *
 *  - **`amountMinor` is absent on a full refund and present on a partial.** The
 *    field's absence *is* the instruction "the whole payment"; computing the whole
 *    amount here would put this process's arithmetic between an operator and
 *    `fares.ride_payments`.
 *  - **A reversal names three things**, because `billing.daily_fee_charges` is
 *    keyed per driver, per vehicle, per day — not one "Driver" box as the
 *    wireframe draws.
 *  - **Rupees become integer minor units**, and `1.1 * 100` must not reach a
 *    contract that says `type: integer`.
 *  - **Each declares its own D-35 action**, and the two are deliberately different
 *    rows: a refund gives a passenger back gateway money, a reversal credits a
 *    driver's prepaid balance.
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const revalidatePath = vi.fn<(path: string) => void>();
const redirect = vi.fn<(url: string) => never>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));
vi.mock('next/navigation', () => ({ redirect: (url: string) => redirect(url) }));
vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createAdminTranslator('en'),
  getLocale: async () => 'en',
}));

const { raiseRefund, reverseFee } = await import('@/server/finance-actions');

const PAYMENT = '0199a1f0-0000-7000-8000-000000000a01';
const DRIVER = '0199a1f0-0000-7000-8000-000000000d01';
const VEHICLE = '0199a1f0-0000-7000-8000-000000000v01'.replace('v', 'e');

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: { refundId: 'r', status: 'Requested' }, status: 200 });
  redirect.mockImplementation((url: string) => {
    throw new Error(`redirect:${url}`);
  });
});

async function raised(values: Record<string, string>) {
  try {
    return await raiseRefund({}, form(values));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('redirect:')) return {};
    throw error;
  }
}

describe('raising a refund', () => {
  it('omits the amount on a full refund, because absence means the whole payment', async () => {
    await raised({ paymentId: PAYMENT, kind: 'full', reasonCode: 'duplicate_charge' });

    expect(mutate.mock.calls[0]?.[0].body).toEqual({
      paymentId: PAYMENT,
      kind: 'full',
      reasonCode: 'duplicate_charge',
    });
  });

  it('omits it on an overpaid reversal too', async () => {
    await raised({ paymentId: PAYMENT, kind: 'overpaid_reversal', reasonCode: 'r19' });

    expect(mutate.mock.calls[0]?.[0].body).not.toHaveProperty('amountMinor');
  });

  it('sends a partial in integer minor units', async () => {
    await raised({ paymentId: PAYMENT, kind: 'partial', amount: '1.10', reasonCode: 'partial' });

    // 1.1 * 100 is 110.00000000000001 in binary floating point, and the contract
    // says `type: integer`.
    expect(mutate.mock.calls[0]?.[0].body).toMatchObject({ amountMinor: 110 });
  });

  it('refuses a partial with no amount rather than sending one it invented', async () => {
    const state = await raised({ paymentId: PAYMENT, kind: 'partial', reasonCode: 'partial' });

    expect(state).toMatchObject({ field: 'amount' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('refuses a mistyped payment id before it reaches a path this process builds', async () => {
    const state = await raised({ paymentId: 'not-an-id', kind: 'full', reasonCode: 'x' });

    expect(state).toMatchObject({ field: 'paymentId' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('requires a reason code, which is kept with the refund', async () => {
    const state = await raised({ paymentId: PAYMENT, kind: 'full' });

    expect(state).toMatchObject({ field: 'reasonCode' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares REFUND_ISSUED against the payment', async () => {
    await raised({ paymentId: PAYMENT, kind: 'full', reasonCode: 'x' });

    expect(mutate.mock.calls[0]?.[0].audit).toEqual({
      action: 'REFUND_ISSUED',
      entity: 'ride_payment',
      entityId: PAYMENT,
    });
  });

  it('returns to the queue saying which status the platform recorded', async () => {
    mutate.mockResolvedValue({ data: { refundId: 'r', status: 'Submitted' }, status: 200 });
    await raised({ paymentId: PAYMENT, kind: 'full', reasonCode: 'x' });

    expect(redirect).toHaveBeenCalledWith('/finance/refunds?raised=Submitted');
  });

  it('puts a refusal in front of the operator in their own language', async () => {
    mutate.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/conflict',
        title: 'Conflict',
        status: 409,
      }),
    );

    const state = await raised({ paymentId: PAYMENT, kind: 'full', reasonCode: 'x' });
    expect(state.message).toBe('Someone changed this first. Reload the page and try again.');
  });
});

describe('reversing a daily fee', () => {
  const OK = {
    driverId: DRIVER,
    vehicleId: VEHICLE,
    feeDate: '2026-06-17',
    reason: 'Crash on Go Online, ticket TK-9012.',
  };

  beforeEach(() => {
    mutate.mockResolvedValue({
      data: {
        entryId: 'e',
        amountMinor: 200_00,
        currency: 'LKR',
        balanceAfterMinor: 1_500_00,
        replayed: false,
      },
      status: 200,
    });
  });

  it('names the driver, the vehicle and the day, because a charge is all three', async () => {
    await reverseFee({}, form(OK));

    expect(mutate.mock.calls[0]?.[0].path).toBe(`/v1/admin/drivers/wallet/${DRIVER}/reverse-fee`);
    expect(mutate.mock.calls[0]?.[0].body).toEqual({
      feeDate: '2026-06-17',
      vehicleId: VEHICLE,
      reason: OK.reason,
    });
  });

  it('omits the amount when the box is empty, meaning the full charged amount', async () => {
    await reverseFee({}, form({ ...OK, amount: '' }));
    expect(mutate.mock.calls[0]?.[0].body).not.toHaveProperty('amountMinor');
  });

  it('sends a partial amount in integer minor units', async () => {
    await reverseFee({}, form({ ...OK, amount: '75.50' }));
    expect(mutate.mock.calls[0]?.[0].body).toMatchObject({ amountMinor: 7550 });
  });

  it.each([
    ['driverId', { ...OK, driverId: 'nope' }],
    ['vehicleId', { ...OK, vehicleId: 'nope' }],
    ['feeDate', { ...OK, feeDate: '17/06/2026' }],
    ['reason', { ...OK, reason: '' }],
  ])('refuses without a usable %s', async (field, values) => {
    const state = await reverseFee({}, form(values));

    expect(state).toMatchObject({ field });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares WALLET_FEE_REVERSED against the wallet, not against the driver', async () => {
    // Its own entity type on purpose: a fee reversal is a fact about the balance,
    // and calling it "driver" would make it indistinguishable from a suspension of
    // the same person when the log is read back.
    await reverseFee({}, form(OK));

    const audit = mutate.mock.calls[0]?.[0].audit ?? { auditedElsewhere: 'iam-svc' as const };
    expect(isAuditIntent(audit)).toBe(true);
    expect(audit).toEqual({
      action: 'WALLET_FEE_REVERSED',
      entity: 'driver_wallet',
      entityId: DRIVER,
    });
  });

  it('tells the operator when their second press did nothing', async () => {
    // The ledger key is the business fact, so a double click replays rather than
    // crediting twice — and `replayed` is on the wire so this can be said.
    mutate.mockResolvedValue({
      data: {
        entryId: 'e',
        amountMinor: 200_00,
        currency: 'LKR',
        balanceAfterMinor: 1_500_00,
        replayed: true,
      },
      status: 200,
    });

    const state = await reverseFee({}, form(OK));
    expect(state.posted?.replayed).toBe(true);
  });

  it('hands back figures already formatted, because the card is a client component', async () => {
    const state = await reverseFee({}, form(OK));

    expect(state.posted?.balanceAfter).toContain('1,500.00');
    expect(state.posted?.amount).toContain('200.00');
  });

  it('re-renders the ledger beside it, because the ledger has moved', async () => {
    await reverseFee({}, form(OK));
    expect(revalidatePath).toHaveBeenCalledWith('/finance/transactions');
  });
});
