import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { PayoutProfile } from '@/api/payout';
import { ProblemError } from '@/api/problem';
import { createFleetTranslator } from '@/i18n';

import { sessionFor, sessionWithoutOrganisation } from './support/fleet';

/**
 * **SCR-FP-002 and SCR-FP-002a on the rendered page** — the component's
 * Definition of Done, where an operator would meet it.
 *
 * The three items that cannot be checked anywhere else are here: a Manager
 * refused SCR-FP-002a by URL, a verified profile that says out loud what saving
 * an edit will do to it, and the Paid gate's explanation appearing exactly when
 * the profile is not verified.
 */

const redirected = new Error('NEXT_REDIRECT');
const forbid = new Error('NEXT_FORBIDDEN');

const redirect = vi.fn((path: string) => {
  (redirected as Error & { path?: string }).path = path;
  throw redirected;
});
const forbidden = vi.fn(() => {
  throw forbid;
});

const getSession = vi.fn();
const read = vi.fn();

vi.mock('next/navigation', () => ({
  redirect: (path: string) => redirect(path),
  forbidden: () => forbidden(),
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('next/cache', () => ({ revalidatePath: vi.fn() }));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/api/client', () => ({ read: (options: unknown) => read(options), mutate: vi.fn() }));

// The three action modules the client panels bind their forms to. A server
// action is a function to `useActionState`, so a stub is enough to render one.
vi.mock('@/server/org-actions', () => ({
  registerOrganisation: vi.fn(),
  inviteMember: vi.fn(),
}));
vi.mock('@/server/payout-actions', () => ({
  savePayoutProfile: vi.fn(),
  uploadPayoutDocument: vi.fn(),
}));
vi.mock('@/server/preferences', () => ({ setAppearance: vi.fn(), setLocale: vi.fn() }));

const { default: OrgSetupPage } = await import('../app/(portal)/org/setup/page');
const { default: TeamPage } = await import('../app/(portal)/org/team/page');
const { default: PayoutPage } = await import('../app/(portal)/org/payout/page');
const { uploadPayoutDocument } = await import('@/server/payout-actions');

const t = createFleetTranslator('en');

const MEMBERS = {
  items: [
    { memberId: '01JQ0000000000000000000000', email: 'ops@lankatransit.lk', fleetRole: 'owner' },
    {
      memberId: '01JQ0000000000000000000002',
      email: 'dispatch@lankatransit.lk',
      name: 'S. Bandara',
      fleetRole: 'manager',
    },
    { memberId: '01JQ0000000000000000000003', email: 'audit@lankatransit.lk', fleetRole: 'viewer' },
  ],
};

function payout(overrides: Partial<PayoutProfile> = {}): PayoutProfile {
  return {
    bank: 'Commercial Bank of Ceylon',
    branch: 'Nugegoda',
    accountNo: '8001234567',
    accountHolderName: 'Lanka Transit (Pvt) Ltd',
    status: 'pending_verification',
    ...overrides,
  };
}

function notFound(): ProblemError {
  return new ProblemError({
    type: 'https://mageride.lk/errors/payout-profile-not-found',
    title: 'payout-profile-not-found',
    status: 404,
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  read.mockResolvedValue(MEMBERS);
});
afterEach(cleanup);

/* ---------------------------------------------------------------------------
 * SCR-FP-002
 * ------------------------------------------------------------------------ */

describe('SCR-FP-002 · organisation setup', () => {
  it('draws the wireframe’s KYC record, its status and the team beside it', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));

    render(await OrgSetupPage());

    // "Org profile & KYC" — the four fields `web_fleet.html` names, plus the two
    // the register route takes.
    for (const label of [
      t('fleet.org.field.name'),
      t('fleet.org.field.registrationNo'),
      t('fleet.org.field.language'),
      t('fleet.org.field.contactPhone'),
    ]) {
      expect(screen.getByText(label), label).toBeTruthy();
    }

    expect(screen.getByText('Lanka Transit (Pvt) Ltd')).toBeTruthy();
    expect(screen.getByText('PV-118842')).toBeTruthy();
    expect(screen.getByText(t('fleet.status.pending'))).toBeTruthy();

    // The team card, with the three seats the wireframe lists.
    expect(screen.getByText(t('fleet.team.heading'))).toBeTruthy();
    expect(screen.getByText(/dispatch@lankatransit\.lk/)).toBeTruthy();
  });

  it('links to SCR-FP-002a for an Owner and for nobody else', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    const owner = render(await OrgSetupPage());
    expect(
      within(owner.container)
        .getAllByRole('link')
        .map((link) => link.getAttribute('href')),
    ).toContain('/org/payout');

    cleanup();

    getSession.mockResolvedValue(sessionFor('manager'));
    const manager = render(await OrgSetupPage());
    expect(
      within(manager.container)
        .queryAllByRole('link')
        .map((link) => link.getAttribute('href')),
    ).not.toContain('/org/payout');
  });

  it('renders the registration form when the account has no organisation yet', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    render(await OrgSetupPage());

    expect(screen.getByText(t('fleet.org.register.heading'))).toBeTruthy();
    // US-13.A7's gate, said before the press rather than discovered after it.
    expect(screen.getByText(t('fleet.org.register.gate'))).toBeTruthy();
    expect(screen.getByRole('button', { name: t('fleet.org.register.submit') })).toBeTruthy();
    // No org, no roster read.
    expect(read).not.toHaveBeenCalled();
  });

  it('states the two things the platform cannot do here rather than drawing them', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    render(await OrgSetupPage());

    // No route edits an organisation, and none uploads an org-level KYC document.
    expect(screen.getByText(t('fleet.org.readOnly'))).toBeTruthy();
    expect(screen.getByText(t('fleet.org.kyc.unavailable'))).toBeTruthy();
  });

  it('shows the invite form to an Owner and the reason to a Manager', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    render(await OrgSetupPage());
    expect(screen.getByRole('button', { name: t('fleet.team.invite.submit') })).toBeTruthy();
    // The two seats an invite may grant, and no third.
    const roles = screen.getAllByRole('option').map((option) => option.textContent);
    expect(roles).toEqual([t('fleet.role.manager'), t('fleet.role.viewer')]);

    cleanup();

    getSession.mockResolvedValue(sessionFor('manager'));
    render(await OrgSetupPage());
    expect(screen.queryByRole('button', { name: t('fleet.team.invite.submit') })).toBeNull();
    expect(screen.getByText(t('fleet.team.invite.ownerOnlyNotice'))).toBeTruthy();
  });

  it('does not lose the screen when the roster read fails', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockRejectedValue(
      new ProblemError({
        type: 'https://mageride.lk/errors/dependency-unavailable',
        title: 'dependency-unavailable',
        status: 503,
      }),
    );

    // A 503 on the team roster is not a reason for the KYC record beside it to
    // disappear, so the page resolves and puts a `<ProblemPanel>` where the
    // roster would be. Asserted on the returned tree rather than on the DOM:
    // `ProblemPanel` is an async server component and only Next can render one.
    // `test/problem.test.ts` covers what it says.
    await expect(OrgSetupPage()).resolves.toBeTruthy();
  });

  it('lets a failure that is not an API problem propagate', async () => {
    // A `TypeError` from this process is a bug, not something to draw a panel
    // about — `app/error.tsx` is where that belongs.
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockRejectedValue(new TypeError('boom'));

    await expect(OrgSetupPage()).rejects.toBeInstanceOf(TypeError);
  });
});

/* ---------------------------------------------------------------------------
 * The team screen
 * ------------------------------------------------------------------------ */

describe('the Team screen', () => {
  it('is the same panel, full width, with the roster it read', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));

    render(await TeamPage());

    expect(screen.getByText(t('fleet.team.heading'))).toBeTruthy();
    expect(screen.getByText(/S\. Bandara/)).toBeTruthy();
    // A Viewer reads the team and is told why they cannot add to it.
    expect(screen.getByText(t('fleet.team.invite.ownerOnlyNotice'))).toBeTruthy();
  });

  it('sends an account with no organisation to the screen that creates one', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    await expect(TeamPage()).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/org/setup');
  });
});

/* ---------------------------------------------------------------------------
 * SCR-FP-002a
 * ------------------------------------------------------------------------ */

describe('SCR-FP-002a · bank & payout details', () => {
  it('refuses a Manager who typed the URL', async () => {
    getSession.mockResolvedValue(sessionFor('manager'));

    await expect(PayoutPage()).rejects.toBe(forbid);
    expect(forbidden).toHaveBeenCalled();
    // Nothing was read on the way to the refusal.
    expect(read).not.toHaveBeenCalled();
  });

  it('refuses a Viewer for the same reason', async () => {
    getSession.mockResolvedValue(sessionFor('viewer'));
    await expect(PayoutPage()).rejects.toBe(forbid);
  });

  it('draws the wireframe’s four fields and both uploads for an Owner', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout());

    render(await PayoutPage());

    for (const label of [
      t('fleet.payout.field.bank'),
      t('fleet.payout.field.branch'),
      t('fleet.payout.field.accountNo'),
      t('fleet.payout.field.holder'),
    ]) {
      expect(screen.getByText(label), label).toBeTruthy();
    }

    // "latest bank statement *or* the first page of the passbook" is one slot
    // with a choice, and the bank-app QR is its own card.
    expect(screen.getByText(t('fleet.payout.proof.heading'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.qr.heading'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.kind.bankStatement'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.kind.passbook'))).toBeTruthy();

    // The account-holder rule is on the field it is about.
    expect(screen.getByText(t('fleet.payout.holderHint'))).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.status.pending'))).toBeTruthy();
  });

  it('warns that saving an edit to a verified profile re-enters verification', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout({ status: 'verified', verifiedAt: '2026-08-01T04:30:00Z' }));

    render(await PayoutPage());

    expect(screen.getByText(t('fleet.status.approved', {}))).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.editVerifiedWarning'))).toBeTruthy();
    // The unverified form's milder line is not also shown.
    expect(screen.queryByText(t('fleet.payout.editWarning'))).toBeNull();
  });

  it('shows the Paid gate’s explanation until the profile is verified', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout({ status: 'pending_verification' }));

    render(await PayoutPage());

    expect(screen.getByText(t('fleet.payout.gate.paid'))).toBeTruthy();
    expect(screen.queryByText(t('fleet.payout.gate.paidReady'))).toBeNull();
  });

  it('and says Paid is available once it is', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout({ status: 'verified' }));

    render(await PayoutPage());

    expect(screen.getByText(t('fleet.payout.gate.paidReady'))).toBeTruthy();
    expect(screen.queryByText(t('fleet.payout.gate.paid'))).toBeNull();
  });

  it('renders the officer’s reason on a rejected profile', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(
      payout({ status: 'rejected', rejectionReason: 'Account holder name does not match KYC' }),
    );

    render(await PayoutPage());

    expect(screen.getByText(/Account holder name does not match KYC/)).toBeTruthy();
    expect(screen.getByText(t('fleet.payout.status.rejected'))).toBeTruthy();
  });

  it('treats "never submitted" as the empty form, not as a failure', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockRejectedValue(notFound());

    render(await PayoutPage());

    expect(screen.getByText(t('fleet.payout.status.none'))).toBeTruthy();
    expect(screen.getByRole('button', { name: t('fleet.payout.submit') })).toBeTruthy();
    // A document is attached to a profile, so the slots wait for one.
    expect(screen.getAllByText(t('fleet.payout.error.profileFirst')).length).toBeGreaterThan(0);
    expect(screen.queryByText(t('fleet.error.title'))).toBeNull();
  });

  it('uploads as soon as a file is chosen, with the kind the chooser is on', async () => {
    // BR-31.1 gives the statement and the passbook page one slot, so the one
    // dropzone the wireframe draws carries the one control the wire needs.
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout());

    const upload = vi.mocked(uploadPayoutDocument);
    upload.mockResolvedValue({ saved: true, uploaded: 'passbook_first_page' });

    const { container } = render(await PayoutPage());

    fireEvent.change(screen.getByLabelText(t('fleet.payout.proof.which')), {
      target: { value: 'passbook_first_page' },
    });

    // The proof slot's own input — Dropzone keeps a real `<input type="file">`
    // under its label rather than a div pretending to be one.
    const input = container.querySelector('input[type="file"]')!;
    const file = new File(['x'], 'passbook.jpg', { type: 'image/jpeg' });
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);

    await waitFor(() => expect(upload).toHaveBeenCalled());

    const body = upload.mock.calls[0]![1];
    expect(body.get('kind')).toBe('passbook_first_page');
    expect((body.get('file') as File).name).toBe('passbook.jpg');
  });

  it('keeps a stored bank the local list does not carry', async () => {
    // `bank` is free text on the wire and the list is this portal's, so a select
    // that silently fell back to the placeholder would let an owner change their
    // bank by correcting a branch name.
    getSession.mockResolvedValue(sessionFor('owner'));
    read.mockResolvedValue(payout({ bank: 'Some Bank That Is Not Listed' }));

    render(await PayoutPage());

    const chosen = screen
      .getAllByRole('option')
      .find((option) => (option as HTMLOptionElement).selected);
    expect(chosen?.textContent).toBe('Some Bank That Is Not Listed');
  });

  it('opens for a pending organisation — the officer reads it before approving', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));
    read.mockResolvedValue(payout());

    render(await PayoutPage());

    expect(screen.getByText(t('fleet.payout.heading'))).toBeTruthy();
  });
});
