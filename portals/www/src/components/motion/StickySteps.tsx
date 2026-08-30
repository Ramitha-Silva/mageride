'use client';

import { useEffect, useRef, useState, type ReactNode } from 'react';

import { cx } from '@mageride/ui';

import { observeIntersection } from '@/lib/motion';

export interface StickyStep {
  /** Stable across renders — the React key and the observer's identity. */
  readonly id: string;
  /** What the sticky column shows while this step is the one being read. */
  readonly media: ReactNode;
  readonly body: ReactNode;
}

/**
 * The "how it works" scroll-through: a column that stays put while the steps beside
 * it scroll past.
 *
 * **Zero JavaScript animation.** `position: sticky` is the browser's own scrolling
 * and costs nothing; the only thing this component does is decide which step is
 * being read, and the only thing that changes is an attribute. There is no
 * scroll-linked transform anywhere in it, which is why it is the one primitive on
 * this surface that behaves identically with reduced motion on and off — and why
 * `app/globals.css` gives it no `prefers-reduced-motion` block rather than an empty
 * one.
 *
 * The active step is the one crossing the middle of the viewport, expressed as a
 * `rootMargin` that shrinks the observation box to a thin band across the centre.
 * That is a more honest reading of "what is being read" than a threshold, which on
 * a tall step never fires and on a short one fires twice.
 */
export function StickySteps({
  steps,
  className,
  mediaClassName,
  stepClassName,
}: {
  readonly steps: readonly StickyStep[];
  readonly className?: string;
  readonly mediaClassName?: string;
  readonly stepClassName?: string;
}) {
  const stepRefs = useRef<(HTMLDivElement | null)[]>([]);
  const [active, setActive] = useState(0);

  useEffect(() => {
    const stops = stepRefs.current.map((element, index) =>
      element
        ? observeIntersection(
            element,
            (entry) => {
              if (entry.isIntersecting) setActive(index);
            },
            // A band across the viewport's middle, 10% tall.
            { rootMargin: '-45% 0px -45% 0px', threshold: 0 },
          )
        : () => {},
    );

    return () => {
      for (const stop of stops) stop();
    };
  }, [steps.length]);

  return (
    <div className={cx('grid gap-lg md:grid-cols-2 md:gap-xl', className)}>
      {/*
        The media column is decorative in the strict sense — it repeats what the
        step beside it already says — so it is `aria-hidden` and the steps carry
        the whole meaning. A reader who never sees the sticky column has lost
        nothing, which is also what happens on a phone, where it is stacked.
      */}
      <div aria-hidden className={cx('mr-sticky-media hidden md:block', mediaClassName)}>
        {steps[active]?.media}
      </div>

      <ol className="flex flex-col gap-section">
        {steps.map((step, index) => (
          <li key={step.id}>
            <div
              ref={(element) => {
                stepRefs.current[index] = element;
              }}
              className={cx('mr-sticky-step', stepClassName)}
              data-mr-active={active === index ? 'true' : 'false'}
            >
              {step.body}
            </div>
          </li>
        ))}
      </ol>
    </div>
  );
}
