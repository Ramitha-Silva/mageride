import { beforeEach, describe, expect, it, vi } from 'vitest';

import { isAuditIntent } from '@/api/audit';
import type { MutateOptions } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { createAdminTranslator } from '@/i18n';

/**
 * SCR-AP-003a's two decisions, as the requests they become (US-2.4a/2.10a/2.15,
 * D-35).
 *
 * The three properties worth holding executable:
 *
 *  - **Confirm carries no value and Edit carries the officer's.** That single
 *    difference is what decides whether the field stays the model's evidence or
 *    becomes `source='manual'` with no confidence, and it is one boolean away
 *    from being wrong in either direction.
 *  - **Approve is not sent while a field is unconfirmed.** The disabled button is
 *    a statement about a page that may be a minute old; this is the second of the
 *    three places that rule lives.
 *  - **Every call declares its D-35 row.** `mutate` requires it, and what is
 *    asserted here is that the row named is the one the action actually causes.
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const revalidatePath = vi.fn<(path: string) => void>();
const redirect = vi.fn<(url: string) => never>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));
vi.mock('next/navigation', () => ({ redirect: (url: string) => redirect(url) }));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createAdminTranslator('en') }));

const { decideField, decideSubject } = await import('@/server/verification-actions');

const SUBJECT = '0199a1f0-0000-7000-8000-000000000001';

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200 });
  redirect.mockImplementation((url: string) => {
    // The real one throws, and the action's control flow depends on that.
    throw new Error(`redirect:${url}`);
  });
});

async function decidedSubject(values: Record<string, string>) {
  try {
    return await decideSubject({}, form(values));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('redirect:')) return {};
    throw error;
  }
}

describe('confirming a field', () => {
  it('sends no value, so the extraction stays the evidence', () => {
    return decideField(
      {},
      form({ subjectId: SUBJECT, subjectType: 'driver', fieldKey: 'nic_no', intent: 'confirm' }),
    ).then(() => {
      expect(mutate).toHaveBeenCalledWith(
        expect.objectContaining({
          method: 'PUT',
          path: `/v1/admin/verification/${SUBJECT}/fields/nic_no`,
          body: {},
        }),
      );
    });
  });

  it('ignores a value typed and then abandoned', async () => {
    // The box is only *used* by Edit & confirm. A value smuggled onto a bare
    // confirm would rewrite the field as a manual entry nobody asked for.
    await decideField(
      {},
      form({
        subjectId: SUBJECT,
        subjectType: 'driver',
        fieldKey: 'nic_no',
        intent: 'confirm',
        value: '1990 99999 999',
      }),
    );

    expect(mutate.mock.calls[0]?.[0].body).toEqual({});
  });

  it('sends the corrected value on Edit & confirm', async () => {
    await decideField(
      {},
      form({
        subjectId: SUBJECT,
        subjectType: 'driver',
        fieldKey: 'nic_no',
        intent: 'edit',
        value: '1990 12345 678',
      }),
    );

    expect(mutate.mock.calls[0]?.[0].body).toEqual({ value: '1990 12345 678' });
  });

  it('refuses an empty correction rather than posting a blank field', async () => {
    const state = await decideField(
      {},
      form({ subjectId: SUBJECT, subjectType: 'driver', fieldKey: 'nic_no', intent: 'edit' }),
    );

    expect(state.message).toContain('Type the corrected value');
    expect(mutate).not.toHaveBeenCalled();
  });

  it('declares the D-35 row the confirmation causes', async () => {
    await decideField(
      {},
      form({ subjectId: SUBJECT, subjectType: 'vehicle', fieldKey: 'reg_no_match', intent: 'confirm' }),
    );

    expect(mutate.mock.calls[0]?.[0].audit).toEqual({
      action: 'VERIFICATION_FIELD_CONFIRMED',
      entity: 'vehicle',
      entityId: SUBJECT,
    });
  });

  it('re-renders the screen, because the last confirmation is what unlocks Approve', async () => {
    await decideField(
      {},
      form({
        subjectId: SUBJECT,
        subjectType: 'driver',
        fieldKey: 'nic_no',
        intent: 'confirm',
        returnTo: `/verification/${SUBJECT}`,
      }),
    );

    expect(revalidatePath).toHaveBeenCalledWith(`/verification/${SUBJECT}`);
  });

  it('puts a refusal in front of the officer in their own language', async () => {
    mutate.mockRejectedValue(
      new ProblemError({ type: 'https://mageride.lk/errors/not-found', title: 'Not Found', status: 404 }),
    );

    const state = await decideField(
      {},
      form({ subjectId: SUBJECT, subjectType: 'driver', fieldKey: 'nic_no', intent: 'confirm' }),
    );

    expect(state.message).toBe('That record no longer exists.');
  });
});

describe('approving and rejecting', () => {
  it('does not send an approval the screen already knows is blocked', async () => {
    const state = await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'driver',
      intent: 'approve',
      approvable: 'false',
    });

    expect(mutate).not.toHaveBeenCalled();
    expect(state).toMatchObject({ message: expect.stringContaining('Approve unlocks') });
  });

  it('approves with no body once nothing is pending', async () => {
    await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'driver',
      intent: 'approve',
      approvable: 'true',
    });

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        path: `/v1/admin/verification/${SUBJECT}/approve`,
        audit: { action: 'VERIFICATION_APPROVED', entity: 'driver', entityId: SUBJECT },
      }),
    );
    expect(mutate.mock.calls[0]?.[0].body).toBeUndefined();
  });

  it('refuses a rejection with no reason (US-2.15)', async () => {
    const state = await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'driver',
      intent: 'reject',
    });

    // "Rejected" with nothing to read leaves somebody unable to fix the one thing
    // between them and driving.
    expect(state).toMatchObject({ field: 'reason' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('sends the reason verbatim, and records it as a refusal', async () => {
    await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'vehicle',
      intent: 'reject',
      reason: 'Plate does not match the registration number.',
    });

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        path: `/v1/admin/verification/${SUBJECT}/reject`,
        body: { reason: 'Plate does not match the registration number.' },
        audit: { action: 'VERIFICATION_REJECTED', entity: 'vehicle', entityId: SUBJECT },
      }),
    );
  });

  it('records a fleet organisation against its own entity type', async () => {
    await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'org',
      intent: 'approve',
      approvable: 'true',
    });

    // Δ C108: `audit` is now a union — a D-35 row, or a declaration that the
    // owning service answers the call and writes none. A verification decision is
    // always the former, and narrowing here says so rather than assuming it.
    const audit = mutate.mock.calls[0]?.[0].audit ?? { auditedElsewhere: 'iam-svc' as const };
    expect(isAuditIntent(audit)).toBe(true);
    expect(isAuditIntent(audit) ? audit.entity : null).toBe('fleet_org');
  });

  it('returns to the queue the officer came from, saying what was decided', async () => {
    await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'driver',
      intent: 'approve',
      approvable: 'true',
      returnTo: '/verification?queue=driving-license&search=ABC',
    });

    expect(redirect).toHaveBeenCalledWith(
      '/verification?queue=driving-license&search=ABC&decided=approved',
    );
  });

  it('will not be redirected off this screen by a form field', async () => {
    // `returnTo` arrives in a form, which means it arrives from whoever wrote the
    // page it was posted from. An absolute URL would make a verdict an open
    // redirect; anything outside this screen is not a queue to come back to.
    await decidedSubject({
      subjectId: SUBJECT,
      subjectType: 'driver',
      intent: 'approve',
      approvable: 'true',
      returnTo: 'https://evil.example/steal',
    });

    expect(redirect).toHaveBeenCalledWith('/verification?decided=approved');
  });

  it('will not put a subject id it did not recognise into a path', async () => {
    const state = await decidedSubject({
      subjectId: '../../admin/users',
      subjectType: 'driver',
      intent: 'approve',
      approvable: 'true',
    });

    expect(mutate).not.toHaveBeenCalled();
    expect(state).toMatchObject({ message: expect.any(String) });
  });
});
