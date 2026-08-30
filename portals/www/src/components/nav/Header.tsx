'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';

import { cx } from '@mageride/ui';

import { localeRelativePath } from '@/lib/routes';

import type { NavLink } from './NavLink';

import { LocaleSwitcher, type LocaleSwitcherLabels } from './LocaleSwitcher';
import { MobileMenu, type MobileMenuLabels } from './MobileMenu';
import { ThemeToggle, type ThemeToggleLabels } from './ThemeToggle';

/**
 * The site header — brand, primary navigation, language, appearance, and the
 * narrow-viewport menu.
 *
 * ## One `<header>`, one `<nav>` per landmark, and the `aria-current` that goes
 * with them
 *
 * S20's a11y test asserts the landmark structure, and the part that is easy to get
 * wrong is not the count but the *naming*: this page ends up with three `<nav>`
 * elements — this one, the language links inside it, and the footer's — and three
 * unnamed ones produce a screen reader landmark list reading "navigation,
 * navigation, navigation". Each carries its own `aria-label` from a resource.
 *
 * The current page's link carries `aria-current="page"`. That is the only way a
 * screen-reader user can tell where they are in a nav whose visual "you are here"
 * is a colour and a weight.
 *
 * ## Sticky, and why `overflow-x-clip` upstream is load-bearing
 *
 * `position: sticky` on this header only works because `app/[locale]/layout.tsx`
 * sets `overflow-x-clip` and **not** `overflow-x-hidden` on the body. `hidden`
 * makes the body a scroll container, which silently breaks `sticky` on every
 * descendant; `clip` refuses the overflow without becoming one. That comment is
 * already in the layout and this is the component that would have been the bug
 * report.
 */
/**
 * The header's own strings and links, plus its three children's (MCS-36 D3).
 *
 * **Nested rather than flattened**, because `Header` is what renders the switcher,
 * the toggle and the menu — so their labels have to reach it, and one prop carrying
 * three named groups reads better than eleven siblings. The nesting is also the
 * honest shape: `app/[locale]/layout.tsx` builds this whole object in one place, and
 * a missing group anywhere in it is a compile error rather than a blank `aria-label`
 * somebody notices in a screen reader six months later.
 */
export interface HeaderLabels {
  /** The wordmark link's accessible name. */
  readonly brandHome: string;
  /** The wordmark itself — a resource, so a Sinhala page could carry a transliteration. */
  readonly brandName: string;
  /** The wide-viewport `<nav>`'s accessible name. */
  readonly primaryNav: string;
  /** The locale-relative home href, for the wordmark. */
  readonly homeHref: string;
  readonly links: readonly NavLink[];
  readonly localeSwitcher: LocaleSwitcherLabels;
  readonly themeToggle: ThemeToggleLabels;
  readonly mobileMenu: MobileMenuLabels;
}

export function Header({ labels }: { readonly labels: HeaderLabels }) {
  const path = localeRelativePath(usePathname() ?? '');

  return (
    <header className="sticky top-0 z-40 border-b border-outline-variant bg-background/85 backdrop-blur">
      <div className="mx-auto flex h-16 max-w-[1200px] items-center gap-md px-4">
        <Link
          href={labels.homeHref}
          aria-label={labels.brandHome}
          className={cx(
            'shrink-0 rounded-sm font-display text-title font-bold text-on-surface',
            'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
          )}
        >
          {/*
            The wordmark is a resource (`www.brand.name`) rather than a literal —
            the key exists so a Sinhala or Tamil page can carry a transliteration
            beside the Latin mark if that is ever wanted. Today all three tables
            say "MageRide", which `test/i18n.test.ts` allow-lists by name.
          */}
          {labels.brandName}
        </Link>

        {/*
          The wide-viewport nav. Hidden below `lg` rather than duplicated into the
          dialog's DOM at every width — see `MobileMenu` for why two renderings of
          one list beats one list repositioned.
        */}
        <nav aria-label={labels.primaryNav} className="hidden flex-1 lg:flex lg:items-center lg:gap-xxs">
          {labels.links.map((route) => {
            const current = route.path === path;
            return (
              <Link
                key={route.path}
                href={route.href}
                aria-current={current ? 'page' : undefined}
                className={cx(
                  'rounded-sm px-xs py-xxs text-body-sm transition-colors',
                  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
                  current
                    ? 'font-medium text-on-surface'
                    : 'text-on-surface-variant hover:text-on-surface',
                )}
              >
                {route.label}
              </Link>
            );
          })}
        </nav>

        <div className="ml-auto flex items-center gap-xxs lg:ml-0">
          <LocaleSwitcher labels={labels.localeSwitcher} />
          <ThemeToggle labels={labels.themeToggle} />
          <MobileMenu labels={labels.mobileMenu} className="lg:hidden" />
        </div>
      </div>
    </header>
  );
}
