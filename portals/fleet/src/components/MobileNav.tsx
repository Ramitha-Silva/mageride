'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { usePathname } from 'next/navigation';

import { CloseGlyph, MenuGlyph } from './icons';

/**
 * The sub-desktop drawer.
 *
 * D2 §FP gives the portal three widths — 375 / 768 / 1024 — and puts the sidebar
 * beside the content at the desktop one. Below it the sidebar becomes this: the
 * same nav, rendered on the server and passed in as `children`, behind a button.
 * A fleet portal is opened on a phone at a depot gate more often than a
 * back-office console is, so this is the common case rather than the fallback.
 *
 * It closes on navigation, on Escape and on a click outside, because all three are
 * how somebody dismisses a drawer and a drawer that survives a tap on the page
 * behind it is a drawer that has eaten the tap.
 */
export function MobileNav({
  openLabel,
  closeLabel,
  children,
}: {
  openLabel: string;
  closeLabel: string;
  children: React.ReactNode;
}) {
  const panelId = useId();
  const closeRef = useRef<HTMLButtonElement>(null);
  const pathname = usePathname();

  // A navigation is a dismissal — and it is *derived* rather than applied in an
  // effect. The drawer remembers which route it was opened on, so arriving
  // somewhere else closes it during the same render that shows the new page,
  // with no second render and no flash of an open drawer over new content.
  const [drawer, setDrawer] = useState({ open: false, path: pathname });
  const open = drawer.open && drawer.path === pathname;
  const setOpen = (value: boolean) => setDrawer({ open: value, path: pathname });

  useEffect(() => {
    if (!open) return;

    closeRef.current?.focus();
    // `setDrawer` rather than the `setOpen` helper above: the helper closes over
    // `pathname` and is rebuilt every render, so depending on it would re-bind
    // this listener on every keystroke elsewhere on the page.
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setDrawer({ open: false, path: pathname });
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, pathname]);

  return (
    <div className="lg:hidden">
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label={openLabel}
        aria-expanded={open}
        aria-controls={panelId}
        className="grid size-10 place-items-center rounded-sm text-on-surface hover:bg-surface-variant"
      >
        <MenuGlyph />
      </button>

      {open ? (
        <div className="fixed inset-0 z-50 flex">
          <button
            type="button"
            aria-label={closeLabel}
            onClick={() => setOpen(false)}
            className="absolute inset-0 bg-on-surface/40"
          />

          <div
            id={panelId}
            className="relative flex h-full w-[280px] max-w-[85vw] flex-col gap-md overflow-y-auto border-e border-outline bg-background p-md shadow-elevation-5"
          >
            <div className="flex justify-end">
              <button
                ref={closeRef}
                type="button"
                onClick={() => setOpen(false)}
                aria-label={closeLabel}
                className="grid size-10 place-items-center rounded-sm text-on-surface hover:bg-surface-variant"
              >
                <CloseGlyph />
              </button>
            </div>
            {children}
          </div>
        </div>
      ) : null}
    </div>
  );
}
