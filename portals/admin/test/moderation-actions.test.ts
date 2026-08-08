import { beforeEach, describe, expect, it, vi } from 'vitest';

import type { MutateOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-004's two decisions, as the requests they become (US-12.6, US-14.3,
 * D-35).
 *
 * The properties worth holding executable:
 *
 *  - **Confirm and Dismiss are one route and two audit rows.** `REPORT_CONFIRMED`
 *    and `REPORT_DISMISSED` are what an auditor filters on; a single
 *    `REPORT_RESOLVED` would make "how many reports were upheld" a question that
 *    needs the JSON image parsed to answer.
 *  - **A suspension is never sent without a reason, and never with an id this
 *    process has not checked.** The first is D-35's other half — a suspension
 *    nobody can explain is one nobody can appeal — and the second is the only
 *    control on the console that takes an identifier from a person.
 *  - **The strike total travels from the answer, not from arithmetic here.**
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const revalidatePath = vi.fn<(path: string) => void>();
const redirect = vi.fn<(url: string) => never>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));
vi.mock('next/navigation', () => ({ redirect: (url: string) => redirect(url) }));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createAdminTranslator('en') }));

const { decideReport, suspendSubject } = await import('@/server/moderation-actions');

const REPORT = '0199a1f0-0000-7000-8000-000000000011';
const VEHICLE = '0199a1f0-0000-7000-8000-0000000000a1';
const DRIVER = '0199a1f0-0000-7000-8000-0000000000d4';

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

/** `redirect` throws in Next, and both actions' control flow depends on it. */
function redirectedTo(): string | null {
  const call = redirect.mock.calls[0]?.[0];
  return call ?? null;
}

async function decided(values: Record<string, string>) {
  try {
    return await decideReport({}, form(values));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('redirect:')) return {};
    throw error;
  }
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: { reportId: REPORT, status: 'CONFIRMED' }, status: 200 });
  redirect.mockImplementation((url: string) => {
    throw new Error(`redirect:${url}`);
  });
});

describe('deciding a report', () => {
  it('confirms one report and declares the row that decision writes', async () => {
    await decided({ reportId: REPORT, intent: 'confirm' });

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        path: `/v1/admin/reports/${REPORT}/resolve`,
        body: { decision: 'CONFIRMED' },
        audit: { action: 'REPORT_CONFIRMED', entity: 'vehicle_report', entityId: REPORT },
      }),
    );
  });

  it('dismisses under its own audit action, not a shared one', async () => {
    await decided({ reportId: REPORT, intent: 'dismiss' });

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        body: { decision: 'DISMISSED' },
        audit: expect.objectContaining({ action: 'REPORT_DISMISSED' }),
      }),
    );
  });

  it('carries the platform’s own confirmed total back to the queue', async () => {
    mutate.mockResolvedValue({
      data: { reportId: REPORT, status: 'CONFIRMED', confirmedCount: 3, vehicleDelisted: true },
      status: 200,
    });

    await decided({ reportId: REPORT, intent: 'confirm' });

    expect(redirectedTo()).toBe('/reports?decided=CONFIRMED&strikes=3&delisted=true');
  });

  it('says nothing about a total the answer did not carry', async () => {
    await decided({ reportId: REPORT, intent: 'confirm' });

    expect(redirectedTo()).toBe('/reports?decided=CONFIRMED');
  });

  it('refuses a report id that is not one, before building a path out of it', async () => {
    const state = await decided({ reportId: 'DRV-55120', intent: 'confirm' });

    expect(mutate).not.toHaveBeenCalled();
    expect(state.message).toBeTruthy();
  });

  it('hands a refusal back as a sentence rather than taking the screen away', async () => {
    mutate.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/conflict',
        title: 'conflict',
        status: 409,
      }),
    );

    const state = await decided({ reportId: REPORT, intent: 'confirm' });

    expect(state.message).toBe(createAdminTranslator('en')('admin.error.conflict'));
    expect(redirect).not.toHaveBeenCalled();
  });
});

describe('suspending a subject', () => {
  it('suspends a vehicle on the vehicle route, with the reason on the body', async () => {
    const state = await suspendSubject(
      {},
      form({ subject: 'vehicle', subjectId: VEHICLE, reason: 'Wrong vehicle photo' }),
    );

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        path: `/v1/admin/vehicles/${VEHICLE}/suspend`,
        body: { reason: 'Wrong vehicle photo' },
        audit: { action: 'VEHICLE_SUSPENDED', entity: 'vehicle', entityId: VEHICLE },
      }),
    );
    expect(state.suspended).toEqual({ subject: 'vehicle', subjectId: VEHICLE });
  });

  it('suspends a driver on the driver route, under the driver’s own audit action', async () => {
    await suspendSubject({}, form({ subject: 'driver', subjectId: DRIVER, reason: 'Rash driving' }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        path: `/v1/admin/drivers/${DRIVER}/suspend`,
        audit: { action: 'DRIVER_SUSPENDED', entity: 'driver', entityId: DRIVER },
      }),
    );
  });

  it('will not send a suspension with no reason', async () => {
    const state = await suspendSubject({}, form({ subject: 'driver', subjectId: DRIVER, reason: '' }));

    expect(mutate).not.toHaveBeenCalled();
    expect(state.field).toBe('reason');
  });

  it('will not put a typed id into a path unless it is an id', async () => {
    const state = await suspendSubject(
      {},
      form({ subject: 'driver', subjectId: 'DRV-55120', reason: 'Rash driving' }),
    );

    expect(mutate).not.toHaveBeenCalled();
    expect(state.field).toBe('subjectId');
  });

  it('carries an Idempotency-Key by leaving it to the data layer, and re-renders the queue', async () => {
    // `mutate` mints one per call; what this asserts is that the action does not
    // opt out of it, and that the screen beside the card is re-read — a suspended
    // vehicle may have left dispatch since the page was drawn.
    await suspendSubject({}, form({ subject: 'vehicle', subjectId: VEHICLE, reason: 'Unsafe' }));

    expect(mutate.mock.calls[0]?.[0].idempotencyKey).toBeUndefined();
    expect(revalidatePath).toHaveBeenCalledWith('/reports');
  });
});
