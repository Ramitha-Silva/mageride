import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ProblemError } from '@/api/problem';
import { createFleetTranslator } from '@/i18n';

import { sessionFor, sessionWithoutOrganisation } from './support/fleet';

/**
 * **The four writes SCR-FP-002 and SCR-FP-002a make**, at the layer that decides
 * what leaves the process.
 *
 * Three properties are asserted on every one of them, because each is a rule the
 * shell holds and a screen can go round: the **URD §2.3 row it declares**, the
 * **target it addresses** (org-relative, so the caller's own `fleetId` is what
 * gets written into the URL), and the **refusal it turns into a sentence** rather
 * than into a stack trace.
 */

const mutate = vi.fn();
const getSession = vi.fn();
const revalidatePath = vi.fn();

vi.mock('@/api/client', () => ({
  mutate: (options: unknown) => mutate(options),
  read: vi.fn(),
}));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createFleetTranslator('en') }));
vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('next/cache', () => ({ revalidatePath: (path: string) => revalidatePath(path) }));

const { registerOrganisation, inviteMember } = await import('@/server/org-actions');
const { savePayoutProfile, uploadPayoutDocument } = await import('@/server/payout-actions');

const t = createFleetTranslator('en');

function form(values: Record<string, string | File>): FormData {
  const data = new FormData();
  for (const [key, value] of Object.entries(values)) data.set(key, value);
  return data;
}

function problem(code: string, status: number): ProblemError {
  return new ProblemError({ type: `https://mageride.lk/errors/${code}`, title: code, status });
}

const ORGANISATION = {
  name: 'Lanka Transit (Pvt) Ltd',
  registrationNo: 'PV-118842',
  contactPhone: '0771234567',
};

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200, idempotencyKey: 'k' });
  getSession.mockResolvedValue(sessionFor('owner'));
});

/* ---------------------------------------------------------------------------
 * POST /v1/fleets
 * ------------------------------------------------------------------------ */

describe('registering an organisation', () => {
  it('posts the one absolute path, declaring that it needs no organisation', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    const state = await registerOrganisation({}, form(ORGANISATION));

    expect(state.done?.name).toBe(ORGANISATION.name);
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        path: '/v1/fleets',
        requires: { area: 'fleet-operations', allowsNoOrganisation: true },
      }),
    );
  });

  it('normalises the mobile to the E.164 form the contract admits', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    await registerOrganisation({}, form({ ...ORGANISATION, contactPhone: '077 123 4567' }));

    expect(mutate.mock.calls[0]![0].body.contactPhone).toBe('+94771234567');
  });

  it('omits the two optional fields rather than sending empty strings', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    await registerOrganisation({}, form({ ...ORGANISATION, contactEmail: '', address: '' }));

    const body = mutate.mock.calls[0]![0].body;
    expect(body).not.toHaveProperty('contactEmail');
    expect(body).not.toHaveProperty('address');
  });

  it('marks the field a refusal is about, and sends nothing when one is empty', async () => {
    expect(await registerOrganisation({}, form({ ...ORGANISATION, name: '' }))).toEqual({
      message: t('fleet.org.error.nameRequired'),
      field: 'name',
    });
    expect(await registerOrganisation({}, form({ ...ORGANISATION, registrationNo: '' }))).toEqual({
      message: t('fleet.org.error.registrationRequired'),
      field: 'registrationNo',
    });
    expect(
      await registerOrganisation({}, form({ ...ORGANISATION, contactPhone: '12345' })),
    ).toEqual({
      message: t('fleet.org.error.phoneInvalid'),
      field: 'contactPhone',
    });

    expect(mutate).not.toHaveBeenCalled();
  });

  it('puts a duplicate business registration on the field it is about', async () => {
    // `ux_fleets_business_reg_active` admits one live application per number, so
    // somebody has already registered this one — possibly them, in another
    // account. That is a refusal an operator can act on without support.
    getSession.mockResolvedValue(sessionWithoutOrganisation());
    mutate.mockRejectedValue(problem('business-registration-exists', 409));

    expect(await registerOrganisation({}, form(ORGANISATION))).toEqual({
      message: t('fleet.error.registrationExists'),
      field: 'registrationNo',
    });
  });

  it('revalidates the whole layout, because the console gains an organisation', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    await registerOrganisation({}, form(ORGANISATION));

    // The nav, the topbar chip and the standing banner are all above this page.
    expect(revalidatePath).toHaveBeenCalledWith('/');
  });
});

/* ---------------------------------------------------------------------------
 * POST /v1/fleets/{id}/members
 * ------------------------------------------------------------------------ */

describe('inviting a team member', () => {
  const INVITE = { email: 'dispatch@lankatransit.lk', fleetRole: 'manager' };

  it('posts to the org-relative roster with the operations row', async () => {
    const state = await inviteMember({}, form(INVITE));

    expect(state.done).toEqual({ name: INVITE.email, role: 'manager' });
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'POST',
        org: '/members',
        requires: { area: 'fleet-operations' },
        body: { email: INVITE.email, fleetRole: 'manager' },
      }),
    );
  });

  it('refuses a Manager, because POST …/members is Owner-only', async () => {
    // The one control on this portal gated on the seat as well as on the URD
    // row — fleet-svc's own `RequireFleetSubRole(Owner)`, not a rule invented
    // here. `canMutate` alone would let a Manager through.
    getSession.mockResolvedValue(sessionFor('manager'));

    expect(await inviteMember({}, form(INVITE))).toEqual({
      message: t('fleet.team.error.ownerOnly'),
    });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('refuses a seat fleet-svc does not admit', async () => {
    expect(await inviteMember({}, form({ ...INVITE, fleetRole: 'owner' }))).toEqual({
      message: t('fleet.team.error.roleRequired'),
      field: 'fleetRole',
    });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('says a duplicate is a colleague who already has a seat', async () => {
    mutate.mockRejectedValue(problem('fleet-member-exists', 409));

    expect(await inviteMember({}, form(INVITE))).toEqual({
      message: t('fleet.error.memberExists'),
      field: 'email',
    });
  });
});

/* ---------------------------------------------------------------------------
 * PUT /v1/fleets/{id}/payout-profile
 * ------------------------------------------------------------------------ */

const BANK = {
  bank: 'Commercial Bank of Ceylon',
  branch: 'Nugegoda',
  accountNo: '8001234567',
  accountHolderName: 'Lanka Transit (Pvt) Ltd',
};

describe('saving the bank details', () => {
  it('PUTs the four fields against the billing row, with no approval gate', async () => {
    // Deliberately no `requiresApprovedOrg`: AL-49's evidence is what the officer
    // reads *before* approving the organisation.
    const state = await savePayoutProfile({}, form(BANK));

    expect(state).toEqual({ saved: true });
    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({
        method: 'PUT',
        org: '/payout-profile',
        body: BANK,
        requires: { area: 'fleet-billing' },
      }),
    );
  });

  it('marks each missing field rather than posting a partial profile', async () => {
    for (const [field, key] of [
      ['bank', 'fleet.payout.error.bankRequired'],
      ['branch', 'fleet.payout.error.branchRequired'],
      ['accountNo', 'fleet.payout.error.accountRequired'],
      ['accountHolderName', 'fleet.payout.error.holderRequired'],
    ] as const) {
      expect(await savePayoutProfile({}, form({ ...BANK, [field]: '' })), field).toEqual({
        message: t(key),
        field,
      });
    }
    expect(mutate).not.toHaveBeenCalled();
  });

  it('does not compare the holder name against the organisation’s own name', async () => {
    // US-27.1 is a requirement on the operator that only the Verification Officer
    // can decide. A portal that enforced it would refuse a sole proprietor's
    // correct personal account.
    const state = await savePayoutProfile(
      {},
      form({ ...BANK, accountHolderName: 'R. M. Perera' }),
    );

    expect(state).toEqual({ saved: true });
    expect(mutate.mock.calls[0]![0].body.accountHolderName).toBe('R. M. Perera');
  });

  it('revalidates the screen, so the chip shows the row that now exists', async () => {
    await savePayoutProfile({}, form(BANK));
    expect(revalidatePath).toHaveBeenCalledWith('/org/payout');
  });
});

/* ---------------------------------------------------------------------------
 * POST /v1/fleets/{id}/payout-profile/documents
 * ------------------------------------------------------------------------ */

describe('uploading a payout document', () => {
  const file = () => new File(['%PDF-1.4'], 'statement.pdf', { type: 'application/pdf' });

  it('sends the multipart body fleet-svc reads, and nothing else', async () => {
    const state = await uploadPayoutDocument({}, form({ kind: 'bank_statement', file: file() }));

    expect(state).toEqual({ saved: true, uploaded: 'bank_statement' });

    const call = mutate.mock.calls[0]![0];
    expect(call.method).toBe('POST');
    expect(call.org).toBe('/payout-profile/documents');
    expect(call.requires).toEqual({ area: 'fleet-billing' });
    expect(call.body).toBeInstanceOf(FormData);
    expect(call.body.get('kind')).toBe('bank_statement');
    expect((call.body.get('file') as File).name).toBe('statement.pdf');
  });

  it('refuses a kind the route does not admit', async () => {
    expect(await uploadPayoutDocument({}, form({ kind: 'registration', file: file() }))).toEqual({
      message: t('fleet.payout.error.kindRequired'),
    });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('refuses an empty file before it leaves the process', async () => {
    expect(
      await uploadPayoutDocument(
        {},
        form({ kind: 'lankaqr_code', file: new File([], 'empty.png') }),
      ),
    ).toEqual({ message: t('fleet.payout.error.fileRequired'), field: 'file' });
  });

  it('turns a file over the ceiling into a sentence rather than a 413', async () => {
    // A courtesy, not a gate: fleet-svc counts the bytes as they arrive.
    const large = new File([new Uint8Array(9 * 1024 * 1024)], 'big.png', { type: 'image/png' });

    const state = await uploadPayoutDocument({}, form({ kind: 'lankaqr_code', file: large }));

    expect(state.field).toBe('file');
    expect(state.message).toBe(t('fleet.error.fileTooLarge', { megabytes: 8 }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it('reads "no profile yet" as an order of operations, not as a failure', async () => {
    // fleet-svc's own words: "a document is attached to a profile, not to an
    // organisation".
    mutate.mockRejectedValue(problem('payout-profile-not-found', 404));

    expect(await uploadPayoutDocument({}, form({ kind: 'lankaqr_code', file: file() }))).toEqual({
      message: t('fleet.payout.error.profileFirst'),
    });
  });
});
