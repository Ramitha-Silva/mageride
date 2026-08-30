'use client';

import { useSyncExternalStore } from 'react';

import { cx } from '@mageride/ui';

import { APPEARANCE_STORAGE_KEY } from '@/lib/appearance';

/**
 * The appearance toggle — a **toggle button**, not two buttons and not a switch.
 *
 * ## Persistence — added in S19
 *
 * A press writes `light` or `dark` to `localStorage` under
 * {@link APPEARANCE_STORAGE_KEY}, and `appearanceScript()` reads it back before the
 * first paint on the next page. Permitted on this surface and on no other: the key
 * and the resolution order live in `src/lib/appearance.ts` with the reasoning, and
 * the short version is that nothing here is sent to a server, so **it is not a
 * cookie** and there is nothing to consent to.
 *
 * **The write is wrapped and its failure is non-fatal.** `localStorage` throws in
 * Safari's private mode and wherever site data is blocked; when it does, the toggle
 * still works for the current page and simply does not survive a navigation. That
 * is the right degradation — refusing to change the theme because it could not be
 * remembered would break the control for the readers most likely to have blocked
 * storage on purpose.
 *
 * ## `aria-pressed` with a stable name
 *
 * The name is "Dark appearance" in every state and `aria-pressed` carries whether
 * it is on. The tempting alternative — a name that flips between "Switch to dark"
 * and "Switch to light" — says the same thing twice to a screen reader and, at the
 * instant of the press, says it in two tenses at once ("Switch to light, pressed").
 * APG's toggle-button pattern is a stable name plus the state, and this is that.
 *
 * ## `useSyncExternalStore`, and why not `useState` + `useEffect`
 *
 * The appearance is **not this component's state**. It lives on `<html>`'s class
 * list, it is put there before the first paint by the inline script in
 * `app/[locale]/layout.tsx`, and it can be changed by the OS setting, by this
 * button, and (from S19) by a stored preference. A component that mirrored it into
 * `useState` would be a second copy of a fact it does not own, kept in step by an
 * effect that writes state on mount — a cascading render, and the thing
 * `react-hooks/set-state-in-effect` correctly refuses.
 *
 * `useSyncExternalStore` is the API for exactly this shape: subscribe to the
 * external source, read a snapshot, and give the server its own answer. The
 * server's snapshot is `false` because a server has neither `matchMedia` nor a DOM
 * — and `false` is the safe direction, because the pre-paint script has already
 * corrected the class by the time anything is visible.
 *
 * The subscription watches **both** sources or the button goes stale: `matchMedia`
 * for the OS setting changing mid-visit, and a `MutationObserver` on the class
 * attribute for the press itself and for anything S19 adds later. Watching only
 * the media query would leave the button showing the wrong state immediately after
 * a reader used it.
 */

/** Subscribe to every source that can change the appearance. Module-level so the reference is stable. */
function subscribeToAppearance(onChange: () => void): () => void {
  const query = window.matchMedia('(prefers-color-scheme: dark)');
  query.addEventListener('change', onChange);

  const observer = new MutationObserver(onChange);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });

  return () => {
    query.removeEventListener('change', onChange);
    observer.disconnect();
  };
}

/** A boolean, so the identity check `useSyncExternalStore` does cannot loop. */
function readAppearance(): boolean {
  return document.documentElement.classList.contains('dark');
}

function readServerAppearance(): boolean {
  return false;
}

/**
 * The one string this control needs, resolved by the server (MCS-36 D3).
 *
 * Exported so `app/[locale]/layout.tsx` builds it against a type rather than by
 * remembering a key — a missing label is a compile error here, which is the same
 * guarantee `en.ts` gives the resource table itself.
 */
export interface ThemeToggleLabels {
  /** The button's accessible name. */
  readonly toggle: string;
}

export function ThemeToggle({
  labels,
  className,
}: {
  readonly labels: ThemeToggleLabels;
  readonly className?: string;
}) {
  const dark = useSyncExternalStore(subscribeToAppearance, readAppearance, readServerAppearance);

  return (
    <button
      type="button"
      aria-pressed={dark}
      aria-label={labels.toggle}
      /*
       * Writes to the DOM, then records the choice. The `MutationObserver` in the
       * subscription above is what tells React the value changed, so there is
       * exactly one source of truth and no way for the button and the page to
       * disagree — the stored value is a *record* of the class, never a second
       * copy React reads.
       */
      onClick={() => {
        const root = document.documentElement;
        const next = !root.classList.contains('dark');
        root.classList.toggle('dark', next);

        try {
          window.localStorage.setItem(APPEARANCE_STORAGE_KEY, next ? 'dark' : 'light');
        } catch {
          // Storage blocked. The toggle still worked; it just will not be
          // remembered. See the note above.
        }
      }}
      /*
       * **`size-11` on the button, `size-cta-icon` on the glyph — the two are not
       * the same measurement, and S19 found them conflated here.**
       *
       * D2's `--spacing-cta-icon` (20px) is the *optical* size of a CTA's leading
       * icon: how big the mark should look beside 16px text. It is not a hit area,
       * and using it as the button's box made this control a 20x20 target, which
       * axe reported against SC 2.5.8 (24x24 minimum) on every page — the header is
       * on all of them. 44px is the number S19 asks for and the one every mobile
       * HIG agrees on; the glyph inside stays at D2's 20px, so nothing about how
       * this looks changes, only how much of it you can hit.
       */
      className={cx(
        'grid size-11 place-items-center rounded-sm text-on-surface-variant',
        'hover:bg-surface-variant hover:text-on-surface',
        'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
        className,
      )}
    >
      {/*
        Two inline SVGs rather than an icon dependency, and `aria-hidden` on both
        because the button's name is the resource above. `currentColor` so they
        follow the text colour in either appearance without a `dark:` variant.
      */}
      <svg viewBox="0 0 20 20" aria-hidden="true" className="size-cta-icon" fill="none">
        {dark ? (
          <path
            d="M16 11.5A6.5 6.5 0 0 1 8.5 4a6.5 6.5 0 1 0 7.5 7.5Z"
            fill="currentColor"
          />
        ) : (
          <>
            <circle cx="10" cy="10" r="3.6" fill="currentColor" />
            <path
              d="M10 2.4v1.8M10 15.8v1.8M17.6 10h-1.8M4.2 10H2.4M15.4 4.6l-1.3 1.3M5.9 14.1l-1.3 1.3M15.4 15.4l-1.3-1.3M5.9 5.9 4.6 4.6"
              stroke="currentColor"
              strokeWidth="1.6"
              strokeLinecap="round"
            />
          </>
        )}
      </svg>
    </button>
  );
}
