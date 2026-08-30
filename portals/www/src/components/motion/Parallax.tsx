'use client';

import { useEffect, useRef, type ReactNode } from 'react';

import { cx } from '@mageride/ui';

import { scheduleFrame } from '@/lib/motion';

import { useReducedMotion } from './useReducedMotion';

/**
 * A device mockup that drifts against the scroll.
 *
 * **It writes a variable, never a rule**, and that distinction is the entire reason
 * this is inside AL-52 while a motion library is not. `.mr-parallax`'s
 * `transform: translate3d(0, var(--mr-www-parallax-y), 0)` is compiled by PostCSS
 * at build; what changes at runtime is one registered `<length>` on one element.
 * Nothing constructs a selector, injects a stylesheet or mutates a rule.
 *
 * `--mr-www-parallax-y` is registered as `<length>` in `app/globals.css`, so a bad
 * write is discarded by the browser and the mockup simply sits still — rather than
 * invalidating the whole `transform` and dropping the element to an untransformed
 * position mid-scroll.
 *
 * **Two elements, because one would measure itself.** The offset is a function of
 * where the element is, and `getBoundingClientRect()` on a translated element
 * reports the position it has already been moved to — so a single-element version
 * feeds its own output back into its input and drifts away down the page. The outer
 * element is never transformed and is what is measured; the inner one moves.
 *
 * Under reduced motion **no listener is attached at all**. The CSS half sets
 * `transform: none` as well, so the two agree from opposite directions: nothing is
 * being written, and nothing already written still applies.
 */
export function Parallax({
  children,
  depth = 0.12,
  className,
}: {
  readonly children: ReactNode;
  /** Fraction of the distance from the viewport's centre. Small is convincing. */
  readonly depth?: number;
  readonly className?: string;
}) {
  const frameRef = useRef<HTMLDivElement>(null);
  const movingRef = useRef<HTMLDivElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    const frame = frameRef.current;
    const moving = movingRef.current;
    if (!frame || !moving || reduced) return;

    const update = () => {
      const rect = frame.getBoundingClientRect();
      const centreOffset = rect.top + rect.height / 2 - window.innerHeight / 2;
      moving.style.setProperty('--mr-www-parallax-y', `${(centreOffset * depth).toFixed(2)}px`);
    };

    // Naive on purpose: `scheduleFrame` coalesces, so a scroll event storm books
    // one callback per frame for the whole document however many mockups are on it.
    const onViewportChange = () => scheduleFrame(update);

    update();
    window.addEventListener('scroll', onViewportChange, { passive: true });
    window.addEventListener('resize', onViewportChange, { passive: true });

    return () => {
      window.removeEventListener('scroll', onViewportChange);
      window.removeEventListener('resize', onViewportChange);
      moving.style.removeProperty('--mr-www-parallax-y');
    };
  }, [depth, reduced]);

  return (
    <div ref={frameRef} className={className}>
      <div ref={movingRef} className={cx('mr-parallax')}>
        {children}
      </div>
    </div>
  );
}
