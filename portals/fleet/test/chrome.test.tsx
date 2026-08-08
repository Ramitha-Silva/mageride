import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { FleetSession } from '@/api/types';
import { SideNav } from '@/components/SideNav';
import { buildNavModel } from '@/components/nav-model';
import { createFleetTranslator, type Locale } from '@/i18n';

import { sessionFor, sessionWithoutOrganisation } from './support/fleet';

/**
 * "Owner / Manager / Viewer sub-roles gate the UI" — asserted on the rendered
 * sidebar, not just on the data behind it.
 *
 * The sessions are derived from URD §2.3 and URD §2.1's fleet sub-model
 * (`./support/fleet.ts`), so what is checked is that the component draws that
 * evaluation and adds nothing.
 */

let pathname = '/dashboard';

vi.mock('next/navigation', () => ({
  usePathname: () => pathname,
}));

vi.mock('next/link', () => ({
  default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(() => {
  cleanup();
  pathname = '/dashboard';
});

function renderNav(session: FleetSession, locale: Locale = 'en') {
  const t = createFleetTranslator(locale);
  return render(
    <SideNav groups={buildNavModel(session, t)} navLabel={t('fleet.nav.label')} />,
  );
}

function hrefs(): string[] {
  return within(screen.getByRole('navigation'))
    .getAllByRole('link')
    .map((link) => link.getAttribute('href')!)
    .sort();
}

describe('the sidebar draws the caller’s own evaluation', () => {
  it('gives an Owner of an approved organisation every screen', () => {
    renderNav(sessionFor('owner'));

    expect(hrefs()).toEqual(
      [
        '/org/setup',
        '/org/payout',
        '/org/team',
        '/dashboard',
        '/vehicles',
        '/drivers',
        '/trackers',
        '/map',
        '/scheduling',
        '/analytics',
        '/billing',
        '/subscriptions',
        '/payments',
      ].sort(),
    );
  });

  it('withholds the money screens from a Manager', () => {
    renderNav(sessionFor('manager'));

    const links = hrefs();
    expect(links).not.toContain('/org/payout');
    expect(links).not.toContain('/billing');
    expect(links).not.toContain('/payments');
    // …and keeps everything the sub-model leaves a Manager.
    expect(links).toContain('/vehicles');
    expect(links).toContain('/subscriptions');
  });

  it('withholds the money screens and the subscriber queue from a Viewer', () => {
    renderNav(sessionFor('viewer'));

    const links = hrefs();
    expect(links).toEqual(
      ['/org/setup', '/org/team', '/dashboard', '/vehicles', '/drivers', '/trackers', '/map', '/scheduling', '/analytics'].sort(),
    );
  });

  it('drops the vehicle and assignment screens while the organisation is pending', () => {
    renderNav(sessionFor('owner', 'PENDING'));

    const links = hrefs();
    expect(links).not.toContain('/vehicles');
    expect(links).not.toContain('/drivers');
    expect(links).not.toContain('/subscriptions');
    // The setup screens are the point of the state, so they are all there.
    expect(links).toContain('/org/setup');
    expect(links).toContain('/org/payout');
  });

  it('offers an account with no organisation exactly one way forward', () => {
    renderNav(sessionWithoutOrganisation());

    expect(hrefs()).toEqual(['/org/setup']);
  });

  it('draws no heading for a group the caller holds nothing in', () => {
    renderNav(sessionFor('viewer', 'PENDING'));

    const headings = within(screen.getByRole('navigation'))
      .getAllByRole('heading')
      .map((heading) => heading.textContent);

    const t = createFleetTranslator('en');
    // Subscriptions is Manager+ and Payments is Owner-only.
    expect(headings).not.toContain(t('fleet.nav.group.subscribers'));
    expect(headings.length).toBeGreaterThan(0);
  });

  it('marks the current entry, and only that one, for a screen reader', () => {
    pathname = '/vehicles/onboard';
    renderNav(sessionFor('owner'));

    const current = within(screen.getByRole('navigation'))
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page')
      .map((link) => link.getAttribute('href'));

    // A nested URL highlights its own screen, by longest prefix.
    expect(current).toEqual(['/vehicles']);
  });
});

describe('every label goes through the translator', () => {
  it('renders the nav in Sinhala and in Tamil', () => {
    for (const locale of ['si', 'ta'] as const) {
      cleanup();
      renderNav(sessionFor('owner'), locale);

      const t = createFleetTranslator(locale);
      const english = createFleetTranslator('en');
      const labels = within(screen.getByRole('navigation'))
        .getAllByRole('link')
        .map((link) => link.textContent ?? '');

      expect(labels).toContain(t('fleet.nav.vehicles'));
      expect(labels).not.toContain(english('fleet.nav.vehicles'));
      expect(screen.getByRole('navigation').getAttribute('aria-label')).toBe(t('fleet.nav.label'));
    }
  });
});
