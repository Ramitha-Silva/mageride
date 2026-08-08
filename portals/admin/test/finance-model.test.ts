import { describe, expect, it } from 'vitest';

import {
  isSettlementMethod,
  raiseRefundHref,
  reconciliationSelection,
  refundSelection,
  transactionsHref,
  transactionsSearch,
  transactionsSelection,
  type RefundQueueRow,
  type SettlementDay,
  type SettlementException,
  type TransactionRow,
} from '@/api/finance';
import {
  exceptionRows,
  findRefundTarget,
  payoutRows,
  refundRows,
  settlementRails,
  settlementTotals,
  transactionRows,
  variancePill,
  type RenderContext,
} from '@/components/finance/model';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-006's arithmetic and its refusals.
 *
 * The properties worth holding executable are the ones where a plausible wrong
 * answer is indistinguishable from the right one on screen: a variance folded the
 * wrong way, a missing ledger posting printed as zero, a transfer counted twice,
 * and a refund offered against a payment that already has one.
 */

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const DRIVER = '0199a1f0-0000-7000-8000-0000000000d1';
const PAYMENT = '0199a1f0-0000-7000-8000-0000000000p1'.replace('p', 'a');

function day(overrides: Partial<SettlementDay> = {}): SettlementDay {
  return {
    businessDate: '2026-06-17',
    method: 'onepay',
    openedCount: 10,
    settledCount: 8,
    failedCount: 1,
    pendingCount: 1,
    settledMinor: 1_000_00,
    postedMinor: 1_000_00,
    varianceMinor: 0,
    currency: 'LKR',
    ...overrides,
  };
}

describe('AL-05 — there are two rails and there is no third', () => {
  it('admits onepay and lankaqr and refuses a bank transfer', () => {
    expect(isSettlementMethod('onepay')).toBe(true);
    expect(isSettlementMethod('lankaqr')).toBe(true);
    expect(isSettlementMethod('bank_transfer')).toBe(false);
    expect(isSettlementMethod('slips')).toBe(false);
  });

  it('drops a method the contract does not admit rather than sending it', () => {
    // admin-bff answers 400 to a third value. A selection that carried one would
    // turn an operator's mistyped bookmark into a validation error about a form
    // they never filled in.
    expect(reconciliationSelection({ method: 'bank_transfer' }).method).toBeUndefined();
    expect(reconciliationSelection({ method: 'lankaqr' }).method).toBe('lankaqr');
  });
});

describe('the settlement fold', () => {
  it('shows the variance admin-bff computed, not its own subtraction', () => {
    // A payload whose `varianceMinor` disagrees with `settled − posted`. Today
    // the two always agree, so the only way to assert that the screen forwards
    // rather than derives is to hand it a day where they do not — the shape a
    // change to what "posted" counts would produce.
    const rails = settlementRails(
      [day({ settledMinor: 1_000_00, postedMinor: 1_000_00, varianceMinor: 250_00 })],
      context,
    );

    expect(rails).toHaveLength(1);
    expect(rails[0]?.variance.tone).toBe('error');
    expect(rails[0]?.variance.label).toContain('250.00');
  });

  it('adds the days of one rail together', () => {
    const rails = settlementRails(
      [
        day({ settledMinor: 2_000_00, postedMinor: 1_900_00, varianceMinor: 100_00 }),
        day({ businessDate: '2026-06-18', settledMinor: 1_000_00, postedMinor: 1_000_00 }),
      ],
      context,
    );

    expect(rails[0]?.settledMoney).toContain('3,000.00');
    expect(rails[0]?.variance.label).toContain('100.00');
  });

  it('gives each rail its own row, and only the rails that appear', () => {
    const rails = settlementRails([day(), day({ method: 'lankaqr' })], context);

    expect(rails.map((rail) => rail.key)).toEqual(['onepay', 'lankaqr']);
    expect(rails[1]?.investigateHref).toBe('/finance/reconciliation?method=lankaqr');
  });

  it('names each Investigate link after its own rail', () => {
    const rails = settlementRails([day({ method: 'lankaqr' })], context);
    expect(rails[0]?.investigateNamed).toContain('LankaQR');
  });
});

describe('a variance of zero is reconciled, and everything else is not', () => {
  it('has no middle band', () => {
    expect(variancePill(0, context).tone).toBe('success');
    expect(variancePill(1, context).tone).toBe('error');
    expect(variancePill(-1, context).tone).toBe('error');
  });

  it('says so in words rather than printing 0.00', () => {
    expect(variancePill(0, context).label).toBe('Reconciled');
    expect(variancePill(-2, context).label).toContain('0.02');
  });

  it('carries the exception count off the summary rather than counting rows', () => {
    const totals = settlementTotals(
      {
        from: '2026-06-01',
        to: '2026-06-30',
        settledMinor: 100,
        postedMinor: 100,
        varianceMinor: 0,
        exceptionCount: 4,
        days: [],
      },
      context,
    );

    expect(totals.hasExceptions).toBe(true);
    expect(totals.exceptions).toContain('4');
    expect(totals.window).toBeTruthy();
  });
});

describe('the exception queue', () => {
  const base: SettlementException = {
    topupId: '0199a1f0-0000-7000-8000-0000000000e1',
    kind: 'settled-not-posted',
    method: 'onepay',
    state: 'Succeeded',
    driverId: DRIVER,
    amountMinor: 5_000_00,
    currency: 'LKR',
    createdAt: '2026-06-17T03:10:00Z',
  };

  it('prints an absent ledger posting as — and never as zero', () => {
    // "Absent is itself the exception on a settled session" — a 0 would read as a
    // posted entry of no value, which is a different and less alarming fact.
    const [row] = exceptionRows([base], context);
    expect(row?.posted).toBe('—');
  });

  it('prints a real posting as money', () => {
    const [row] = exceptionRows([{ ...base, postedMinor: 4_000_00 }], context);
    expect(row?.posted).toContain('4,000.00');
  });

  it('keeps the order admin-bff sent, because the queue is worked oldest first', () => {
    const rows = exceptionRows(
      [
        { ...base, topupId: 'a', createdAt: '2026-06-01T00:00:00Z' },
        { ...base, topupId: 'b', createdAt: '2026-06-17T00:00:00Z' },
      ],
      context,
    );

    expect(rows.map((row) => row.key)).toEqual(['a', 'b']);
  });

  it('tones the one no retry fixes differently from the rest', () => {
    expect(exceptionRows([{ ...base, kind: 'amount-mismatch' }], context)[0]?.kind.tone).toBe('error');
    expect(exceptionRows([{ ...base, kind: 'unsettled' }], context)[0]?.kind.tone).toBe('warning');
  });

  it('falls back to the driver id when the join found no name', () => {
    expect(exceptionRows([base], context)[0]?.driver).toBe(DRIVER);
    expect(exceptionRows([{ ...base, driverName: 'Nuwan' }], context)[0]?.driver).toBe('Nuwan');
  });
});

describe('the wallet ledger', () => {
  const entry: TransactionRow = {
    entryId: '0199a1f0-0000-7000-8000-0000000000f1',
    kind: 'driver_transfer',
    amountMinor: 2_500_00,
    currency: 'LKR',
    fromPartyId: DRIVER,
    fromAccountType: 'driver',
    toPartyId: '0199a1f0-0000-7000-8000-0000000000d2',
    toAccountType: 'driver',
    ts: '2026-06-17T03:10:00Z',
  };

  it('draws one row per money event, not one per account leg', () => {
    // The whole reason the report reads the journal: the projection would give a
    // transfer two rows and double the platform's transfer volume when summed.
    expect(transactionRows([entry], context)).toHaveLength(1);
  });

  it('names the platform singleton by its account type, having no id to show', () => {
    const [row] = transactionRows(
      [{ ...entry, kind: 'topup', fromPartyId: undefined, fromAccountType: 'platform' }],
      context,
    );

    expect(row?.from).toBe('MageRide');
  });

  it('prefers a joined name, then the id', () => {
    expect(transactionRows([{ ...entry, fromName: 'Nuwan' }], context)[0]?.from).toBe('Nuwan');
    expect(transactionRows([entry], context)[0]?.from).toBe(DRIVER);
  });
});

describe('the transactions query is built once, for three renderings', () => {
  it('carries the same parameters to the screen, the CSV and the PDF', () => {
    const selection = transactionsSelection({
      from: '2026-06-01',
      to: '2026-06-30',
      kind: 'topup',
    });

    expect(transactionsSearch(selection)).toEqual({
      from: '2026-06-01',
      to: '2026-06-30',
      kind: 'topup',
      limit: 100,
    });

    const csv = transactionsHref('/finance/transactions/export/csv', selection);
    const pdf = transactionsHref('/finance/transactions/export/pdf', selection);

    expect(csv).toContain('from=2026-06-01');
    expect(csv).toContain('kind=topup');
    expect(pdf.replace('/pdf', '/csv')).toBe(csv);
  });

  it('drops a half-chosen window rather than sending one open end', () => {
    expect(transactionsSelection({ from: '2026-06-01' }).from).toBeUndefined();
  });

  it('rejects a date that matches the pattern and is not a day', () => {
    expect(transactionsSelection({ from: '2026-02-31', to: '2026-03-01' }).from).toBeUndefined();
  });
});

describe('the refund queue', () => {
  const overpaid: RefundQueueRow = {
    source: 'overpaid',
    paymentId: PAYMENT,
    rideId: '0199a1f0-0000-7000-8000-0000000000r1',
    paymentState: 'Overpaid',
    method: 'wallet',
    amountMinor: 1_200_00,
    paymentAmountMinor: 1_200_00,
    currency: 'LKR',
    requestedAt: '2026-06-17T03:10:00Z',
  };

  const raised: RefundQueueRow = {
    ...overpaid,
    source: 'refund',
    refundId: '0199a1f0-0000-7000-8000-0000000000b1',
    status: 'Requested',
    kind: 'full',
  };

  it('offers the raise form on an unraised overpayment and not on a raised refund', () => {
    // R-19's whole point: the row nobody has acted on is the one that needs a
    // control, and a second refund against the same payment is a 409.
    const rows = refundRows([overpaid, raised], {}, context);

    expect(rows[0]?.raiseHref).toContain(`paymentId=${PAYMENT}`);
    expect(rows[1]?.raiseHref).toBeNull();
  });

  it('keys an overpaid row on its payment, having no refund id to key on', () => {
    expect(refundRows([overpaid], {}, context)[0]?.key).toBe(`overpaid:${PAYMENT}`);
  });

  it('distinguishes the two populations rather than merging them', () => {
    const rows = refundRows([overpaid, raised], {}, context);
    expect(rows[0]?.source.label).not.toBe(rows[1]?.source.label);
  });

  it('states the payment ceiling beside the refund amount', () => {
    const [row] = refundRows([{ ...raised, amountMinor: 200_00 }], {}, context);
    expect(row?.amount).toContain('200.00');
    expect(row?.paymentAmount).toContain('1,200.00');
  });

  it('carries the current filter into the raise link', () => {
    const href = raiseRefundHref({ source: 'overpaid', status: 'Requested' }, PAYMENT);
    expect(href).toContain('source=overpaid');
    expect(href).toContain('status=Requested');
    expect(href.endsWith('#raise')).toBe(true);
  });

  it('finds only an unraised target, so a stale bookmark cannot aim at a refunded payment', () => {
    expect(findRefundTarget([overpaid], PAYMENT)).toBe(overpaid);
    expect(findRefundTarget([raised], PAYMENT)).toBeNull();
    expect(findRefundTarget([overpaid], undefined)).toBeNull();
  });

  it('reads the queue filters off the URL and ignores what the contract does not admit', () => {
    const selection = refundSelection({ source: 'overpaid', status: 'Nope', paymentId: 'x' });
    expect(selection.source).toBe('overpaid');
    expect(selection.status).toBeUndefined();
    expect(selection.paymentId).toBeUndefined();
  });
});

describe('payouts', () => {
  it('shows a failure reason and offers nothing to press', () => {
    // C133 removed the retry route: the debit is already reversed, so there is
    // nothing to re-send. The view model carries a reason and no action.
    const [row] = payoutRows(
      [
        {
          payoutId: '0199a1f0-0000-7000-8000-0000000000c1',
          batchId: '0199a1f0-0000-7000-8000-0000000000c0',
          driverId: DRIVER,
          amountMinor: 15_000_00,
          currency: 'LKR',
          status: 'FAILED',
          failureReason: 'Account closed',
          createdAt: '2026-06-17T03:10:00Z',
        },
      ],
      context,
    );

    expect(row?.status.tone).toBe('error');
    expect(row?.failureReason).toBe('Account closed');
    expect(row).not.toHaveProperty('retryHref');
  });
});
