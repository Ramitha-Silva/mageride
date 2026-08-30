'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useState } from 'react';

import { cx, Modal } from '@mageride/ui';

import { localeRelativePath } from '@/lib/routes';

import type { NavLink } from './NavLink';

/**
 * The narrow-viewport navigation — a trigger, and a dialog holding the same links
 * the wide header shows inline.
 *
 * ## The focus trap is Radix's, deliberately
 *
 * `@mageride/ui`'s `Modal` wraps Radix Dialog, and S14's brief says to use it
 * rather than hand-rolling a trap. That is not a convenience: a correct trap has
 * to handle the first and last focusable elements, `Tab` and `Shift+Tab`, focus
 * restoration to the trigger on close, `Escape`, an `aria-modal` boundary, inert
 * background content and a scroll lock — and it has to keep working when a link
 * inside is added or removed. Every hand-rolled version of that in the wild is
 * missing at least one. AL-52 permits Radix by name for exactly this reason: the
 * trap is *behaviour*, not styling, and Radix emits no CSS.
 *
 * So this component owns three things and no more: whether the dialog is open,
 * what is inside it, and **closing it on navigation**. That last one is the
 * failure Radix cannot know about — a client-side route change leaves the dialog
 * mounted and open over the new page, and the reader has to dismiss a menu they
 * have already used.
 *
 * ## Why the links are duplicated rather than moved
 *
 * The wide header renders the same routes inline. Two renderings of one list is
 * the usual smell, but the alternative — one list, repositioned with CSS — means
 * the dialog's links exist in the DOM on every viewport, inside a hidden
 * container, where a screen reader's link list finds thirteen phantom entries.
 * Both renderings read `routesInGroup`, so there is still exactly one list; what
 * differs is where it is shown. `test/routes.test.ts` is what keeps that list
 * honest.
 */
/** The dialog's four strings and its link list, resolved on the server (MCS-36 D3). */
export interface MobileMenuLabels {
  /** The trigger's accessible name. */
  readonly open: string;
  readonly title: string;
  readonly close: string;
  /** The `<nav>` inside the dialog — named, because the page already has two others. */
  readonly primaryNav: string;
  /** Primary and support routes, in that order. */
  readonly links: readonly NavLink[];
}

export function MobileMenu({
  labels,
  className,
}: {
  readonly labels: MobileMenuLabels;
  readonly className?: string;
}) {
  const pathname = usePathname() ?? '';
  const path = localeRelativePath(pathname);
  const [open, setOpen] = useState(false);

  return (
    <>
      <button
        type="button"
        aria-label={labels.open}
        aria-expanded={open}
        onClick={() => setOpen(true)}
        /*
         * **44px hit box, 20px glyph — the same conflation S19 found on
         * `ThemeToggle`, and worse here.**
         *
         * `size-cta-icon` is D2's 20px *icon* box: how big a mark should look
         * beside 16px text. Used as the button's own size it made the one control
         * that opens navigation on a phone a 20x20 target — the width where this
         * button is the *only* way to reach any other page, and the width where
         * every reader is using a thumb.
         *
         * axe passes it on the spacing exception, because nothing sits close
         * enough to it for the 24px circles to collide. Passing SC 2.5.8 by being
         * lonely is not the same as being tappable, which is why S19 asks for 44
         * and why this is fixed rather than filed.
         */
        className={cx(
          'grid size-11 place-items-center rounded-sm text-on-surface-variant',
          'hover:bg-surface-variant hover:text-on-surface',
          'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
          className,
        )}
      >
        <svg viewBox="0 0 20 20" aria-hidden="true" className="size-cta-icon">
          <path
            d="M3 5.5h14M3 10h14M3 14.5h14"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.75"
            strokeLinecap="round"
          />
        </svg>
      </button>

      <Modal
        open={open}
        onOpenChange={setOpen}
        title={labels.title}
        closeLabel={labels.close}
      >
        {/*
          A `<nav>` inside the dialog, named — the page already has the header's
          and the footer's, and three unnamed navigation landmarks in a screen
          reader's list are three identical entries.
        */}
        <nav aria-label={labels.primaryNav} className="flex flex-col gap-xxs">
          {labels.links.map((route) => {
            const current = route.path === path;
            return (
              <Link
                key={route.path}
                href={route.href}
                aria-current={current ? 'page' : undefined}
                onClick={() => setOpen(false)}
                className={cx(
                  'rounded-sm px-sm py-xs text-body transition-colors',
                  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
                  current
                    ? 'bg-surface-variant font-medium text-on-surface'
                    : 'text-on-surface-variant hover:bg-surface-variant hover:text-on-surface',
                )}
              >
                {route.label}
              </Link>
            );
          })}
        </nav>
      </Modal>
    </>
  );
}
