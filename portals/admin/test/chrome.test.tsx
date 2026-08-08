import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { buildNavModel } from '@/components/nav-model';
import { SideNav } from '@/components/SideNav';
import { createAdminTranslator } from '@/i18n';

import { menuFor } from './support/urd';

/**
 * "A Support/CSR session sees only the nav entries URD §2.3 permits" — asserted
 * on the rendered sidebar, not just on the data behind it.
 *
 * The menu is derived from the URD's own §2.3 table and admin-bff's own manifest
 * (`./support/urd.ts`), so what is checked is that the component draws that and
 * adds nothing.
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

afterEach(cleanup);

function renderNav(roles: Parameters<typeof menuFor>[0], locale: 'si' | 'ta' | 'en' = 'en') {
  const t = createAdminTranslator(locale);
  return render(<SideNav groups={buildNavModel(menuFor(roles), t)} navLabel={t('admin.nav.label')} />);
}

describe('the sidebar draws the server-filtered menu', () => {
  it('shows a Support/CSR exactly their permitted entries', () => {
    renderNav(['support_csr']);

    const nav = screen.getByRole('navigation');
    const links = within(nav)
      .getAllByRole('link')
      .map((link) => link.getAttribute('href'));

    expect(links.sort()).toEqual(
      [
        '/dashboard',
        '/verification',
        '/verification/expiring',
        '/passengers',
        '/drivers',
        '/vehicles',
        '/reports',
        '/support/tickets',
        '/moderation/fraud',
        '/finance/refunds',
      ].sort(),
    );
  });

  it('draws no heading for a group the caller holds nothing in', () => {
    renderNav(['support_csr']);

    const nav = screen.getByRole('navigation');
    const headings = within(nav)
      .getAllByRole('heading')
      .map((heading) => heading.textContent);

    // An empty "Configuration" heading would tell a CSR that a Configuration
    // section exists and they cannot reach it — information the nav has no
    // reason to leak, which is why admin-bff drops the group rather than
    // sending it empty.
    expect(headings).not.toContain('Configuration');
    expect(headings).not.toContain('Access');
    expect(headings).toContain('Directory');
  });

  it('shows a Verification Officer their queues and no directory, finance or config', () => {
    renderNav(['verification_officer']);

    const links = within(screen.getByRole('navigation'))
      .getAllByRole('link')
      .map((link) => link.getAttribute('href'));

    // URD §2.3's Moderation row gives VER `◐ at onboarding`, so the two review
    // queues open alongside the two onboarding ones — one cell wider than D2
    // §AP's "onboarding queue only" shorthand.
    expect(links.sort()).toEqual(
      ['/moderation/fraud', '/reports', '/verification', '/verification/expiring'].sort(),
    );
  });

  it('marks the current screen, and only that one', () => {
    pathname = '/passengers/01JQ0000000000000000000000';
    renderNav(['support_csr']);

    const current = within(screen.getByRole('navigation'))
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.getAttribute('href')).toBe('/passengers');
    pathname = '/dashboard';
  });

  it('marks the nested screen rather than its parent', () => {
    pathname = '/verification/expiring';
    renderNav(['verification_officer']);

    const current = within(screen.getByRole('navigation'))
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]?.getAttribute('href')).toBe('/verification/expiring');
    pathname = '/dashboard';
  });

  it('renders every label in Sinhala and Tamil too', () => {
    for (const locale of ['si', 'ta'] as const) {
      const { unmount } = renderNav(['super_admin'], locale);
      const nav = screen.getByRole('navigation');

      for (const link of within(nav).getAllByRole('link')) {
        const text = link.textContent?.trim() ?? '';
        expect(text, `${locale} label for ${link.getAttribute('href')}`).not.toBe('');
        // A raw resource key reaching the sidebar is the failure mode
        // `translateServerKey` degrades to; it must never actually happen.
        expect(text.startsWith('nav.')).toBe(false);
      }

      unmount();
    }
  });
});
