'use client';

import { useCallback, useEffect, useRef, useState } from 'react';

import { cx, Modal } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import type { ScreenEntry } from '@/content/screens';

/**
 * **The** lightbox. One implementation, used by the home showcase (S15) and by
 * every screen reference inside a guide chapter (S17).
 *
 * S17's fence says so in as many words — *"Reuse the lightbox; do not build a
 * second one"* — and it was extracted from `ScreenCarousel` to make that possible
 * rather than merely intended. A second implementation is not a duplication
 * problem, it is an **accessibility** problem: the six obligations below took a
 * measurement pass each to get right, and a copy would have to get all six right
 * again, silently, in a component nobody re-tested.
 *
 * ## The six obligations, and which are ours
 *
 * Radix (through `@mageride/ui`'s `Modal`) provides: focus moves in, focus is
 * trapped, `Escape` closes, the page behind does not scroll, and it is a real
 * dialog rather than a `div` with `role="dialog"`.
 *
 * **Two are this component's**, because Radix has no opinion about either:
 *
 *   - **←/→ move between images and the position is announced.** The `alt` tells a
 *     screen reader *what* the screen is and nothing about *where in the set* it
 *     is, so after pressing ← twice a reader has no way to know anything moved.
 *   - **Focus returns to the trigger on close.** Radix restores focus to its own
 *     `Dialog.Trigger`, and this dialog is *controlled* — `open`/`onOpenChange`,
 *     no trigger — so there is nothing for it to restore to. Measured in S15:
 *     `document.activeElement` was `<body>` after `Escape`, and stayed `<body>`.
 *     The trigger is captured on open and focused on close inside
 *     `requestAnimationFrame`, because a synchronous focus is undone by Radix's
 *     own teardown a frame later.
 *
 * ## `useLightbox` rather than props threading
 *
 * The two call sites are shaped differently — a carousel owns a list and knows the
 * index it opened, a chapter has screens scattered through its steps — so the hook
 * owns the state and the component owns the dialog. Both get the same keyboard
 * handling and the same announcement without either re-deriving it.
 */
export interface LightboxController {
  readonly openIndex: number | null;
  readonly announcement: string;
  /** Opens at `index`, remembering the element to hand focus back to. */
  readonly open: (index: number, trigger: HTMLElement | null) => void;
  readonly close: () => void;
  readonly move: (delta: number) => void;
}

/**
 * Every string the dialog renders, resolved on the server (MCS-36 D3).
 *
 * `positions` and `captions` are **arrays indexed by slide**, which is the part worth
 * explaining. The live-region announcement is `"Screen 3 of 12"` — parameterised by
 * an index only the client knows — so it looks like a string that cannot be resolved
 * ahead of time. It can: the *count* is known at build, so the server resolves all N
 * of them and the client picks one. No template, no placeholder substitution and no
 * translator on this side of the boundary.
 */
export interface LightboxLabels {
  readonly title: string;
  readonly close: string;
  readonly previous: string;
  readonly next: string;
  /** One announcement per slide index — `positions[i]` describes slide `i`. */
  readonly positions: readonly string[];
  /** One caption per slide index, aligned with the `screens` array. */
  readonly captions: readonly string[];
}

export function useLightbox(positions: readonly string[]): LightboxController {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const triggerRef = useRef<HTMLElement | null>(null);

  const announce = useCallback(
    (index: number) => {
      setAnnouncement(positions[index] ?? '');
    },
    [positions],
  );

  const open = useCallback(
    (index: number, trigger: HTMLElement | null) => {
      triggerRef.current = trigger;
      setOpenIndex(index);
      announce(index);
    },
    [announce],
  );

  const close = useCallback(() => {
    setOpenIndex(null);
    const trigger = triggerRef.current;
    if (!trigger) return;
    requestAnimationFrame(() => trigger.focus());
  }, []);

  const move = useCallback(
    (delta: number) => {
      setOpenIndex((current) => {
        if (current === null) return current;
        // `positions` is one entry per slide, so its length IS the count — the same
        // number `useLightbox(locale, count)` used to take as its own argument.
        const count = positions.length;
        const next = (current + delta + count) % count;
        announce(next);
        return next;
      });
    },
    [announce, positions],
  );

  /*
   * ←/→ while open. A `document` listener rather than `onKeyDown` on a wrapper
   * `<div>`: a key handler on a non-interactive element is what
   * `jsx-a11y/no-static-element-interactions` refuses, and it is right to — the div
   * is not focusable, so it only ever receives these by bubbling from whichever
   * button holds focus, which stops working the moment focus is on the image.
   *
   * The usual objection to a document listener does not apply while a **modal**
   * dialog is open: Radix traps focus inside, so nothing else can consume an arrow
   * key until it closes, and the listener is removed when it does.
   */
  useEffect(() => {
    if (openIndex === null) return;

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'ArrowRight') {
        event.preventDefault();
        move(1);
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        move(-1);
      }
    };

    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [openIndex, move]);

  return { openIndex, announcement, open, close, move };
}

export function ScreenLightbox({
  labels,
  screens,
  controller,
}: {
  readonly labels: LightboxLabels;
  readonly screens: readonly ScreenEntry[];
  readonly controller: LightboxController;
}) {
  const { openIndex, announcement, close, move } = controller;
  const index = openIndex ?? -1;
  const current = screens[index];

  return (
    <Modal
      open={openIndex !== null}
      onOpenChange={(next) => {
        if (!next) close();
      }}
      title={labels.title}
      closeLabel={labels.close}
      className={cx('max-w-[min(92vw,32rem)]', 'print-hidden')}
    >
      <div className="flex flex-col gap-md">
        {current ? (
          <>
            <ScreenImage
              screen={current}
              alt={labels.captions[index] ?? ''}
              priority
              sizes="min(92vw, 32rem)"
              className="mx-auto w-full"
            />
            <p className="text-body-sm text-on-surface-variant">{labels.captions[index]}</p>
          </>
        ) : null}

        <div className="flex items-center justify-between gap-md">
          <button
            type="button"
            onClick={() => move(-1)}
            className="min-h-cta rounded-lg border border-outline px-md py-xs text-body-sm text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
          >
            {labels.previous}
          </button>
          <button
            type="button"
            onClick={() => move(1)}
            className="min-h-cta rounded-lg border border-outline px-md py-xs text-body-sm text-on-surface focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
          >
            {labels.next}
          </button>
        </div>
      </div>

      {/*
        `sr-only`, not hidden: a live region with `display: none` is not in the
        accessibility tree and announces nothing at all.
      */}
      <p aria-live="polite" aria-atomic="true" className="sr-only">
        {announcement}
      </p>
    </Modal>
  );
}
