import { describe, expect, it } from 'vitest';

import { moderationSelection, suspendHref, type ReportRow } from '@/api/moderation';
import {
  queueTotal,
  reportRows,
  verdictNotice,
  type RenderContext,
} from '@/components/moderation/model';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-004's view model, and the one number on the screen that is easy to get
 * wrong in a way nobody would notice.
 *
 * **A pending report is not a strike.** Three *confirmed* reports delist a vehicle
 * (US-12.6); the queue holds the ones nobody has decided. `ReportRow.confirmedCount`
 * is `null` on every row admin-bff answers with, so the only honest count this
 * screen can print is how many pending reports a vehicle has — and printing it as
 * a strike total would tell a moderator they were one press from delisting
 * somebody when they might be three.
 */

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

const VEHICLE_A = '0199a1f0-0000-7000-8000-0000000000a1';
const VEHICLE_B = '0199a1f0-0000-7000-8000-0000000000b2';

function report(reportId: string, vehicleId: string, over: Partial<ReportRow> = {}): ReportRow {
  return {
    reportId,
    vehicleId,
    reason: 'Rash driving',
    status: 'PENDING',
    createdAt: '2026-06-17T03:10:00Z',
    ...over,
  };
}

describe('the reports queue', () => {
  it('counts the pending reports each vehicle has, not the strikes it has taken', () => {
    const rows = reportRows(
      [
        report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A),
        report('0199a1f0-0000-7000-8000-000000000002', VEHICLE_A),
        report('0199a1f0-0000-7000-8000-000000000003', VEHICLE_B),
      ],
      context,
    );

    expect(rows.map((row) => row.pending.label)).toEqual(['2 pending', '2 pending', '1 pending']);
  });

  it('never prints a confirmed total, because the queue is never given one', () => {
    // The contract declares `confirmedCount` and admin-bff answers null for it.
    // A row that happened to carry one still must not be drawn as the strike
    // figure: the screen's number is the pending count and says so.
    const [row] = reportRows([report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A, {
      confirmedCount: 2,
    })], context);

    expect(row?.pending.label).toBe('1 pending');
  });

  it('tones a repeatedly reported vehicle without implying it is about to be delisted', () => {
    const one = reportRows([report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A)], context);
    const three = reportRows(
      [
        report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A),
        report('0199a1f0-0000-7000-8000-000000000002', VEHICLE_A),
        report('0199a1f0-0000-7000-8000-000000000003', VEHICLE_A),
      ],
      context,
    );

    expect(one[0]?.pending.tone).toBe('neutral');
    expect(three[0]?.pending.tone).toBe('error');
  });

  it('says when a reporter left no words rather than drawing an empty cell', () => {
    const [row] = reportRows(
      [report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A, { reason: '   ' })],
      context,
    );

    expect(row?.reason).toBeNull();
  });

  it('aims the suspend card at the row’s own vehicle', () => {
    const [row] = reportRows([report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A)], context);

    expect(row?.suspendHref).toBe(suspendHref('vehicle', VEHICLE_A));
    expect(row?.suspendHref).toContain('#suspend');
  });

  it('names each row’s buttons after the vehicle they act on', () => {
    const [row] = reportRows([report('0199a1f0-0000-7000-8000-000000000001', VEHICLE_A)], context);

    expect(row?.confirmNamed).toContain(VEHICLE_A);
    expect(row?.dismissNamed).toContain(VEHICLE_A);
    expect(row?.confirmNamed).not.toBe(row?.dismissNamed);
  });
});

describe('the waiting count', () => {
  it('is what one page answered, and says so when there is another', () => {
    const page = (items: number, hasMore: boolean) => ({
      items: Array.from({ length: items }, (_, index) =>
        report(`0199a1f0-0000-7000-8000-00000000000${index}`, VEHICLE_A),
      ),
      cursor: null,
      hasMore,
    });

    expect(queueTotal(page(3, false), context)).toBe('3 waiting');
    expect(queueTotal(page(3, true), context)).toBe('3+ waiting');
  });

  it('is an em dash when the queue failed — never a zero', () => {
    // "Nothing is waiting" and "nobody answered" are different facts, and a 0
    // that means the second is the one a moderator goes home on.
    expect(queueTotal(null, context)).toBe('—');
  });
});

describe('the verdict notice', () => {
  it('says nothing when nothing was decided', () => {
    expect(verdictNotice({}, context)).toBeNull();
  });

  it('reports a dismissal as the row that was written', () => {
    expect(verdictNotice({ decided: 'DISMISSED' }, context)).toMatchObject({
      tone: 'success',
      action: 'REPORT_DISMISSED',
    });
  });

  it('counts down to the delisting when the platform handed back a total', () => {
    const notice = verdictNotice({ decided: 'CONFIRMED', strikes: 2 }, context);

    expect(notice?.message).toContain('2 confirmed reports');
    expect(notice?.message).toContain('1 more');
    expect(notice?.tone).toBe('success');
  });

  it('says the vehicle is delisted, and only when the platform says it is', () => {
    const notice = verdictNotice({ decided: 'CONFIRMED', strikes: 3, delisted: true }, context);

    expect(notice?.message).toContain('delisted');
    expect(notice?.tone).toBe('error');
  });

  it('confirms without a number when the answer carried none', () => {
    const notice = verdictNotice({ decided: 'CONFIRMED' }, context);

    expect(notice?.message).toBe('Report confirmed.');
  });
});

describe('the URL is the screen’s state', () => {
  it('reads the subject a row aimed the suspend card at', () => {
    expect(moderationSelection({ subject: 'vehicle', subjectId: VEHICLE_A })).toEqual({
      subject: 'vehicle',
      subjectId: VEHICLE_A,
    });
  });

  it('drops an id that is not one, rather than putting it in a path', () => {
    expect(moderationSelection({ subject: 'driver', subjectId: '../../etc' })).toEqual({
      subject: 'driver',
    });
  });

  it('drops a verdict nobody could have recorded', () => {
    expect(moderationSelection({ decided: 'DELETED', strikes: '2' })).toEqual({ strikes: 2 });
  });
});
