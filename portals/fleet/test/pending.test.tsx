import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createFleetTranslator } from '@/i18n';

import { sessionFor, sessionWithoutOrganisation } from './support/fleet';

/**
 * "An unapproved org sees the pending-verification state" — the Definition of
 * Done item, on the rendered page rather than on the routing decision behind it.
 *
 * The state is drawn twice on purpose: as a standing banner over every screen
 * (`OrgStatusBanner`) and as the page an approval-gated URL is rewritten to. The
 * first stops a console looking merely empty; the second is what somebody who
 * followed a bookmark to `/vehicles` actually reads.
 */

const redirected = new Error('NEXT_REDIRECT');
const redirect = vi.fn((path: string) => {
  (redirected as Error & { path?: string }).path = path;
  throw redirected;
});
const getSession = vi.fn();

vi.mock('next/navigation', () => ({ redirect: (path: string) => redirect(path) }));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
}));

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));

const { OrgStatusBanner } = await import('@/components/OrgStatusBanner');
// A relative specifier because `@/…` is `src/`, and the App Router tree is not
// under it — the route group's parentheses are part of the directory name.
const { default: PendingPage } = await import('../app/(portal)/pending/page');

const t = createFleetTranslator('en');

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);

describe('the standing banner', () => {
  it('tells a pending organisation why its console is short of screens', async () => {
    render(await OrgStatusBanner({ session: sessionFor('owner', 'PENDING') }));

    expect(screen.getByRole('status').textContent).toContain(t('fleet.banner.pending'));
  });

  it('gives a rejected organisation the officer’s own reason', async () => {
    const session = sessionFor('owner', 'REJECTED');
    const rejected = {
      ...session,
      organisation: { ...session.organisation!, rejectionReason: 'Business registration expired' },
    };

    render(await OrgStatusBanner({ session: rejected }));

    const banner = screen.getByRole('status');
    expect(banner.textContent).toContain(t('fleet.banner.rejected'));
    expect(banner.textContent).toContain('Business registration expired');
  });

  it('says a Viewer’s session is read-only, so an absent button is not a bug', async () => {
    render(await OrgStatusBanner({ session: sessionFor('viewer') }));

    expect(screen.getByRole('status').textContent).toContain(t('fleet.banner.viewer'));
  });

  it('says nothing at all to an Owner of an approved organisation', async () => {
    const banner = await OrgStatusBanner({ session: sessionFor('owner') });
    expect(banner).toBeNull();
  });

  it('shows the pending state rather than the read-only one when both are true', async () => {
    // Two banners about two reasons the same button is missing is worse than the
    // more urgent one on its own.
    render(await OrgStatusBanner({ session: sessionFor('viewer', 'PENDING') }));

    expect(screen.getByRole('status').textContent).toContain(t('fleet.banner.pending'));
    expect(screen.queryByText(t('fleet.banner.viewer'))).toBeNull();
  });
});

describe('the page an approval-gated URL is rewritten to', () => {
  it('explains the wait, names what is blocked, and links to what is open', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));

    render(await PendingPage());

    expect(screen.getByRole('heading').textContent).toBe(t('fleet.pending.title'));
    expect(screen.getByText(t('fleet.pending.blocked'))).toBeTruthy();
    expect(screen.getByText(t('fleet.pending.next'))).toBeTruthy();

    const links = screen.getAllByRole('link').map((link) => link.getAttribute('href'));
    expect(links).toContain('/org/setup');
    expect(links).toContain('/org/payout');
    // The two screens the gate closes are not offered as somewhere to go.
    expect(links).not.toContain('/vehicles');
    expect(links).not.toContain('/drivers');
  });

  it('turns into the rejection notice, with the reason, when the org was refused', async () => {
    const session = sessionFor('owner', 'REJECTED');
    getSession.mockResolvedValue({
      ...session,
      organisation: { ...session.organisation!, rejectionReason: 'Owner NIC did not match' },
    });

    render(await PendingPage());

    expect(screen.getByRole('heading').textContent).toBe(t('fleet.rejected.title'));
    expect(screen.getByText(/Owner NIC did not match/)).toBeTruthy();
    // The "what you can do meanwhile" line belongs to a wait, not to a refusal.
    expect(screen.queryByText(t('fleet.pending.next'))).toBeNull();
  });

  it('shows a Viewer only the screens a Viewer can open', async () => {
    getSession.mockResolvedValue(sessionFor('viewer', 'PENDING'));

    render(await PendingPage());

    const links = screen.getAllByRole('link').map((link) => link.getAttribute('href'));
    expect(links).toContain('/org/setup');
    expect(links).not.toContain('/org/payout');
  });

  it('sends an approved organisation away — the usual way here is a stale bookmark', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    await expect(PendingPage()).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/dashboard');
  });

  it('sends an account with no organisation to the screen that creates one', async () => {
    getSession.mockResolvedValue(sessionWithoutOrganisation());

    await expect(PendingPage()).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/org/setup');
  });

  it('sends a signed-out caller to sign in', async () => {
    getSession.mockResolvedValue(null);

    await expect(PendingPage()).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/login');
  });
});

describe('the status pill', () => {
  it('names the state in the operator’s own language', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));

    const { container } = render(await PendingPage());
    expect(within(container).getByText(t('fleet.status.pending'))).toBeTruthy();
  });
});
