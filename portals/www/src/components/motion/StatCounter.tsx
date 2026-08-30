'use client';

import { useEffect, useRef, type CSSProperties } from 'react';

import { cx } from '@mageride/ui';

import type { Locale } from '@/i18n/locales';
import { observeIntersection } from '@/lib/motion';

import { useReducedMotion } from './useReducedMotion';

/** The curve, if the stylesheet cannot be read — the same value `--mr-www-ease-out` holds. */
const FALLBACK_EASING = 'cubic-bezier(0.22, 1, 0.36, 1)';

/**
 * A number that counts up when it scrolls into view.
 *
 * `Element.animate()` interpolates `--mr-www-count`, which `app/globals.css`
 * registers as an `<integer>` — registration is what makes a custom property
 * animatable at all — and `counter()` renders it. **No React state, no frame
 * callback and no re-render**: the number is counted by the same engine that runs
 * the page's transitions, which is why this costs nothing on a mid-range Android
 * even with several on screen.
 *
 * The easing is read back from `--mr-www-ease-out` rather than restated, so the
 * counter cannot end up on a different curve from everything else on the page.
 *
 * **Two numbers are rendered, and only one of them moves.** Generated content is
 * not reliably announced, and `counter()` cannot group thousands — so the animated
 * digits are `aria-hidden` and a screen reader is given the finished value,
 * formatted for the reader's own locale, in an `sr-only` span. That is also the
 * value anyone with `@property` unsupported sees, because the component writes it
 * outright whenever it cannot animate.
 *
 * Under reduced motion there is nothing to disable in CSS: the component simply
 * sets the final value and never calls `animate()`.
 *
 * **A number on a public page is a factual claim** (README rule 7). Whatever fills
 * `value` needs a spec anchor in the content module that supplies it.
 */
export function StatCounter({
  locale,
  value,
  label,
  suffix = '',
  durationMs = 1600,
  className,
}: {
  readonly locale: Locale;
  readonly value: number;
  /**
   * The stat's caption, already translated (MCS-36 D3).
   *
   * `locale` stays a prop beside it and is not a leftover: `Intl.NumberFormat` needs
   * it to group digits, and a locale *string* costs nothing — it is importing the
   * translator that costs 88 kB.
   */
  readonly label: string;
  /**
   * A unit that belongs *to the number* — "%" on "0% commission" (S15).
   *
   * A prop rather than a sibling at the call site, because `counter()` renders the
   * digits and cannot concatenate: the suffix has to be laid out beside the
   * animated span, which is inside this component, and it has to join the
   * `sr-only` value so a screen reader hears "0%" and not a bare "0" — which on a
   * commission figure is the difference between the claim and its opposite.
   */
  readonly suffix?: string;
  readonly durationMs?: number;
  readonly className?: string;
}) {
  const formatted = `${new Intl.NumberFormat(`${locale}-LK`).format(value)}${suffix}`;
  const ref = useRef<HTMLSpanElement>(null);
  const reduced = useReducedMotion();

  useEffect(() => {
    const element = ref.current;
    if (!element) return;

    const settle = () => element.style.setProperty('--mr-www-count', String(value));

    if (reduced || typeof element.animate !== 'function') {
      settle();
      return;
    }

    /*
     * Wind back to zero, now that we know we can animate (S15 §7).
     *
     * The element is server-rendered carrying its **real** value — see the inline
     * `style` below — so a reader with JavaScript off, and a crawler, see "10" and
     * not "0". That is a requirement rather than a nicety: the number is the
     * claim, and a claim that only exists after hydration is a claim most
     * measurement tools never see.
     *
     * The cost of being correct there is that a hydrated reader would otherwise
     * see the final value and then watch it snap to zero when the section scrolls
     * in. So the wind-back happens here, at mount, before the observer is armed —
     * one frame, on a band that is section seven of nine and therefore below the
     * fold at every width this site supports.
     */
    element.style.setProperty('--mr-www-count', '0');

    let stop = () => {};
    stop = observeIntersection(
      element,
      (entry) => {
        if (!entry.isIntersecting) return;
        stop();

        const easing =
          getComputedStyle(element).getPropertyValue('--mr-www-ease-out').trim() || FALLBACK_EASING;

        // `Keyframe` has no index signature for custom properties, which is a gap
        // in the DOM types rather than in the API — WAAPI accepts any registered
        // custom property as an animatable key.
        const animation = element.animate(
          [{ '--mr-www-count': 0 }, { '--mr-www-count': value }] as unknown as Keyframe[],
          { duration: durationMs, easing, fill: 'forwards' },
        );

        // `fill: 'forwards'` holds the end value; this makes it the element's own,
        // so the animation can be discarded and the number survives a repaint.
        animation.addEventListener('finish', settle);
      },
      { threshold: 0.4 },
    );

    return () => stop();
  }, [value, durationMs, reduced]);

  return (
    <div className={className}>
      <span className="block font-display text-hero-sm text-secondary">
        {/*
          The announced value first, so it is what reaches assistive technology,
          and the animated digits second and `aria-hidden`. A pseudo-element
          cannot carry `aria-hidden` itself, which is why `.mr-counter-value` is
          on a span of its own rather than on the block above.
        */}
        <span className="sr-only">{formatted}</span>
        {/*
          The inline custom property is what puts the **real** number in the
          server-rendered HTML (S15 §7). `counter()` reads
          `--mr-www-count`, whose registered `initial-value` is 0, so without this
          the visible digits are "0" until an effect runs — which is what a JS-off
          reader and most crawlers would keep.
        */}
        <span
          ref={ref}
          aria-hidden
          className={cx('mr-counter-value')}
          style={{ '--mr-www-count': value } as CSSProperties}
        />
        {suffix ? <span aria-hidden>{suffix}</span> : null}
      </span>
      <span className="block text-body-sm text-on-surface-variant">{label}</span>
    </div>
  );
}
