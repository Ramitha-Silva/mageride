import { describe, expect, it } from 'vitest';

import {
  isBusinessDate,
  statsHref,
  statsSearch,
  statsSelection,
  STATS_PERIODS,
} from '@/api/dashboard';

/**
 * SCR-AP-002's period filter, as the pure function that turns a URL into a query
 * (AL-38, US-24.7).
 *
 * The interesting cases are all about **not** inventing a window. C061 refuses to
 * substitute a default for an incomplete custom range — "a `custom` range missing
 * its dates that quietly answered for today would put the wrong number under the
 * right heading, and the operator would have no way to tell" — and a portal that
 * substituted one first would defeat that refusal before the request was made.
 */

describe('the four periods', () => {
  it('is exactly the contract enum', () => {
    // `admin-bff.yaml#/components/parameters/StatsPeriod`, in its order. A fifth
    // value here would be a segmented control with a button nothing answers.
    expect([...STATS_PERIODS]).toEqual(['today', 'week', 'month', 'custom']);
  });

  it('defaults to today, which is the contract default', () => {
    expect(statsSelection({})).toEqual({ period: 'today', awaitingRange: false });
  });

  it('falls back to today for a period nobody defined', () => {
    // Not the substitution above: there is no window `?period=quarter` could have
    // meant, so there is no figure to put under the wrong heading.
    expect(statsSelection({ period: 'quarter' }).period).toBe('today');
  });

  it('reads the first value when a parameter arrives twice', () => {
    expect(statsSelection({ period: ['week', 'month'] }).period).toBe('week');
  });
});

describe('a custom range', () => {
  it('carries both ends once both parse', () => {
    expect(statsSelection({ period: 'custom', from: '2026-06-01', to: '2026-06-28' })).toEqual({
      period: 'custom',
      from: '2026-06-01',
      to: '2026-06-28',
      awaitingRange: false,
    });
  });

  it('waits rather than asking, when only one end has been chosen', () => {
    const selection = statsSelection({ period: 'custom', from: '2026-06-01' });

    expect(selection.awaitingRange).toBe(true);
    // Deliberately not `today`: the screen shows the date form, not a month of
    // figures under a heading that says "Custom range".
    expect(selection.period).toBe('custom');
    expect(selection.to).toBeUndefined();
  });

  it('waits on a date that is not a date, however it was typed', () => {
    for (const to of ['tomorrow', '28-06-2026', '2026-6-1', '2026-02-31', '']) {
      expect(statsSelection({ period: 'custom', from: '2026-06-01', to }).awaitingRange).toBe(true);
    }
  });

  it('leaves an impossible-but-complete range to admin-bff', () => {
    // `to` before `from` is policy — the read model refuses it by name, and a
    // second copy of that rule here is a second place it can drift.
    expect(statsSelection({ period: 'custom', from: '2026-06-28', to: '2026-06-01' })).toMatchObject(
      { awaitingRange: false },
    );
  });
});

describe('isBusinessDate', () => {
  it('accepts a real calendar date and rejects one the pattern alone admits', () => {
    expect(isBusinessDate('2026-02-28')).toBe(true);
    expect(isBusinessDate('2024-02-29')).toBe(true);
    expect(isBusinessDate('2026-02-29')).toBe(false);
    expect(isBusinessDate('2026-13-01')).toBe(false);
    expect(isBusinessDate(undefined)).toBe(false);
  });
});

describe('the query the screen and the download share', () => {
  it('sends only the period on the three fixed windows', () => {
    // admin-bff resolves those ranges. A `from` in the URL the figures do not
    // reflect is a URL somebody later reads as evidence of what was on screen.
    expect(statsSearch(statsSelection({ period: 'week', from: '2026-01-01' }))).toEqual({
      period: 'week',
    });
  });

  it('sends both ends on a custom one', () => {
    expect(
      statsSearch(statsSelection({ period: 'custom', from: '2026-06-01', to: '2026-06-28' })),
    ).toEqual({ period: 'custom', from: '2026-06-01', to: '2026-06-28' });
  });

  it('gives the export the same query the page was rendered with', () => {
    // The DoD's "the CSV contains exactly the filtered figures on screen" rests on
    // this: one query, and admin-bff renders both from one service call.
    const selection = statsSelection({ period: 'custom', from: '2026-06-01', to: '2026-06-28' });

    expect(statsHref('/dashboard/export', selection)).toBe(
      '/dashboard/export?period=custom&from=2026-06-01&to=2026-06-28',
    );
    expect(new URL(statsHref('/dashboard', selection), 'https://admin.mageride.lk').search).toBe(
      new URL(statsHref('/dashboard/export', selection), 'https://admin.mageride.lk').search,
    );
  });
});
