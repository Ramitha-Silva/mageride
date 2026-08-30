'use client';

import { Children, useCallback, useState, type ReactNode } from 'react';

import { cx } from '@mageride/ui';

import { observeIntersection } from '@/lib/motion';

/**
 * Content that fades — and optionally rises — as it scrolls into view.
 *
 * One `IntersectionObserver` entry flips one attribute; `app/globals.css`'s
 * `.mr-reveal` does the rest, and the reduced-motion variant sits in the same rule
 * block so the two cannot drift apart.
 *
 * **The container is what is observed, not the children.** A staggered grid of
 * nine cards is one observation and nine integers — each wrapper carries `--i`, and
 * the CSS multiplies it by the stagger step — rather than nine observations racing
 * each other into view. It also means a stagger reads in source order regardless of
 * how the grid wraps.
 *
 * **The reveal is one-way.** It unobserves itself on the first intersection, so
 * content does not re-hide when it leaves the viewport: an element that fades out
 * behind you and back in when you scroll up is both nauseating and the single most
 * common way this effect is done badly.
 *
 * Where `IntersectionObserver` does not exist the content reveals immediately. A
 * browser that cannot say when an element is on screen must not be handed a page
 * that stays at `opacity: 0` until it can — and the CSS half agrees, hiding the
 * pre-reveal state only inside `@media (scripting: enabled)`.
 */
export function Reveal({
  children,
  variant = 'rise',
  stagger = false,
  className,
}: {
  readonly children: ReactNode;
  /** `rise` fades and translates; `fade` only fades. Reduced motion forces `fade`. */
  readonly variant?: 'rise' | 'fade';
  /** Give each direct child its own `--i` delay. Wraps every child in one element. */
  readonly stagger?: boolean;
  readonly className?: string;
}) {
  const [revealed, setRevealed] = useState(false);

  /*
   * A **ref callback with a cleanup**, and not a `useEffect`.
   *
   * What is being synchronised here is the lifetime of a DOM node, not the result
   * of a render, and React 19 lets a ref callback say so directly — it runs when
   * the node attaches and its return value runs when it detaches. Written as an
   * effect this would have to `setRevealed(true)` synchronously in the effect body
   * for the no-`IntersectionObserver` case, which is the cascading-render pattern
   * `react-hooks/set-state-in-effect` exists to catch, and it would be a fair
   * catch.
   *
   * Initialising the state to `true` instead would be worse again: the server has
   * no `IntersectionObserver` either, so it would render `revealed` and the client
   * would render hidden, and every reveal on the page would be a hydration
   * mismatch.
   */
  const observe = useCallback((element: HTMLDivElement | null) => {
    if (!element) return;

    if (typeof IntersectionObserver === 'undefined') {
      setRevealed(true);
      return;
    }

    let stop = () => {};
    stop = observeIntersection(
      element,
      (entry) => {
        if (!entry.isIntersecting) return;
        setRevealed(true);
        stop();
      },
      // A tenth of the element, and only once it is clear of the fold — so a
      // section does not spend its animation in the bottom 40px of the screen.
      { threshold: 0.1, rootMargin: '0px 0px -10% 0px' },
    );

    return () => stop();
  }, []);

  const state = revealed ? 'true' : 'false';

  if (!stagger) {
    return (
      <div
        ref={observe}
        className={cx('mr-reveal', className)}
        data-mr-revealed={state}
        data-mr-variant={variant}
      >
        {children}
      </div>
    );
  }

  return (
    <div ref={observe} className={className}>
      {Children.toArray(children).map((child, index) => (
        <div
          key={index}
          className="mr-reveal"
          data-mr-revealed={state}
          data-mr-variant={variant}
          style={{ '--i': index } as React.CSSProperties}
        >
          {child}
        </div>
      ))}
    </div>
  );
}
