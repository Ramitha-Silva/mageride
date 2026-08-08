import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createFleetTranslator } from '@/i18n';
import { PUBLIC_PATHS } from '@/server/routes';

import { sessionFor } from './support/fleet';

/**
 * **SCR-FP-001 · `fleet_login_signup`** — one card, two tabs, two routes.
 *
 * `web_fleet.html` draws this screen at `/signup` with a **Create account**
 * button, and its states line reads "sign-up vs login". Both halves exist here;
 * what the second one cannot have is the button, because nothing on any contract
 * registers a Fleet Portal identity. So the assertions below are in two parts:
 * the tab is present and says how an account really comes to exist, **and** no
 * control on it posts anywhere.
 *
 * The card is rendered directly rather than through its two pages, for the reason
 * `chrome.test.tsx` renders `OrgStatusBanner` directly: it is an async server
 * component, and only Next can render one nested inside another component's
 * tree. What the pages themselves decide — which tab, and where an
 * already-signed-in caller goes instead — is asserted on what they return.
 */

const redirected = new Error('NEXT_REDIRECT');
const redirect = vi.fn((path: string) => {
  (redirected as Error & { path?: string }).path = path;
  throw redirected;
});
const getSession = vi.fn();

vi.mock('next/navigation', () => ({ redirect: (path: string) => redirect(path) }));

vi.mock('@/i18n/server', () => ({
  getTranslator: async () => createFleetTranslator('en'),
  getLocale: async () => 'en',
}));

vi.mock('@/server/session', () => ({ getSession: () => getSession() }));
vi.mock('@/server/auth-actions', () => ({ signIn: vi.fn(), signOut: vi.fn() }));

const { AuthScreen } = await import('@/components/auth/AuthScreen');
const { default: LoginPage } = await import('../app/login/page');
const { default: SignUpPage } = await import('../app/signup/page');

const t = createFleetTranslator('en');

/** Neither route takes a parameter in most of these tests. */
const noParams = Promise.resolve({});

beforeEach(() => {
  vi.clearAllMocks();
  getSession.mockResolvedValue(null);
});
afterEach(cleanup);

describe('the card the wireframe draws', () => {
  it('has both of the wireframe’s halves, as tabs', async () => {
    render(await AuthScreen({ tab: 'signIn' }));

    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);
    expect(tabs).toEqual([t('fleet.auth.tab.signIn'), t('fleet.auth.tab.signUp')]);
    expect(screen.getByRole('tab', { selected: true }).textContent).toBe(
      t('fleet.auth.tab.signIn'),
    );
  });

  it('opens on the tab it was asked for', async () => {
    render(await AuthScreen({ tab: 'signUp' }));

    expect(screen.getByRole('tab', { selected: true }).textContent).toBe(
      t('fleet.auth.tab.signUp'),
    );
  });

  it('is the same card at both of the wireframe’s addresses', async () => {
    // `/login` is the shell's; `/signup` is the address bar `web_fleet.html`
    // actually draws. Two routes onto one component, differing in one prop.
    expect((await LoginPage({ searchParams: noParams })).props.tab).toBe('signIn');
    expect((await SignUpPage({ searchParams: noParams })).props.tab).toBe('signUp');
  });

  it('serves both addresses signed out, and nothing else new', async () => {
    // Adding to the public set is the one place an accident is a hole, so the
    // list is asserted whole rather than by membership.
    expect(PUBLIC_PATHS).toContain('/login');
    expect(PUBLIC_PATHS).toContain('/signup');
    expect(PUBLIC_PATHS).toHaveLength(6);
  });

  it('carries the credential form and AL-37’s missing second factor', async () => {
    render(await AuthScreen({ tab: 'signIn' }));

    expect(screen.getByRole('button', { name: t('fleet.signIn.submit') })).toBeTruthy();
    expect(screen.getByText(t('fleet.signIn.noSecondFactor'))).toBeTruthy();
    // No self-service reset exists, and the screen says so rather than linking.
    expect(screen.getByText(t('fleet.signIn.forgot'))).toBeTruthy();
  });

  it('reports a failed federated sign-in and a completed sign-out', async () => {
    render(await AuthScreen({ tab: 'signIn', signedOut: true, failedProvider: 'apple' }));

    expect(screen.getByText(t('fleet.signIn.signedOut'))).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toContain('Apple');
  });
});

describe('the Create account tab states what the platform cannot do', () => {
  it('says so, and offers the two paths that actually exist', async () => {
    render(await AuthScreen({ tab: 'signUp' }));

    expect(screen.getByText(t('fleet.signUp.unavailable'))).toBeTruthy();
    // Invited by an existing owner, or taken on by MageRide.
    expect(screen.getByText(t('fleet.signUp.byOwner'))).toBeTruthy();
    expect(screen.getByText(t('fleet.signUp.byMageRide'))).toBeTruthy();
    // …and then SCR-FP-002 registers the organisation itself.
    expect(screen.getByText(t('fleet.signUp.thenOrg'))).toBeTruthy();
  });

  it('draws no control that would post nowhere', async () => {
    const { container } = render(await AuthScreen({ tab: 'signUp' }));

    // A Create-account button that failed would be worse than the sentence that
    // replaced it: an operator cannot tell a broken control from an absent
    // feature. Radix keeps only the selected panel mounted, so this is the
    // sign-up half on its own.
    const panel = screen.getByRole('tabpanel');
    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('input')).toBeNull();
    expect(panel.querySelector('button[type="submit"]')).toBeNull();
    expect(container.querySelector('a[href*="signup"]')).toBeNull();
  });

  it('names the verification, reset and link/unlink walls in the same place', async () => {
    render(await AuthScreen({ tab: 'signUp' }));

    expect(screen.getByText(t('fleet.signUp.verification'))).toBeTruthy();
    expect(screen.getByText(t('fleet.signUp.identities'))).toBeTruthy();
  });
});

describe('both routes send a signed-in caller on', () => {
  it('from /login', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    await expect(LoginPage({ searchParams: noParams })).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/dashboard');
  });

  it('from /signup, which is the same card', async () => {
    getSession.mockResolvedValue(sessionFor('owner', 'PENDING'));

    await expect(SignUpPage({ searchParams: noParams })).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/org/setup');
  });

  it('honouring a safe ?next=, and only a safe one', async () => {
    getSession.mockResolvedValue(sessionFor('owner'));

    await expect(LoginPage({ searchParams: Promise.resolve({ next: '/vehicles' }) })).rejects.toBe(
      redirected,
    );
    expect((redirected as Error & { path?: string }).path).toBe('/vehicles');

    await expect(
      LoginPage({ searchParams: Promise.resolve({ next: 'https://evil.example/x' }) }),
    ).rejects.toBe(redirected);
    expect((redirected as Error & { path?: string }).path).toBe('/dashboard');
  });
});
