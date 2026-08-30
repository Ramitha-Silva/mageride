'use client';

import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react';

import { cx } from '@mageride/ui';

import { observeIntersection, prefersReducedMotion } from '@/lib/motion';

import { useReducedMotion } from './useReducedMotion';

const AUTOPLAY_INTERVAL_MS = 6000;

/**
 * The sliding hero — autoplay, swipe, keyboard, and a dot per slide.
 *
 * **The platform's own scroller does most of it.** `scroll-snap-type: x mandatory`
 * on the track gives swipe, momentum and the snap for free, on every phone, with no
 * gesture handler and no dependency; an `IntersectionObserver` over the slides
 * reports which one is showing, so the dots follow a *drag* as faithfully as they
 * follow a tap. The component adds only the three things a scroller has no opinion
 * about: a timer, arrow keys, and a pause control.
 *
 * S14 owns the hero itself and the full APG carousel pattern; what is settled here
 * is the mechanism and the accessibility floor.
 *
 * ### Autoplay stops under reduced motion. It does not merely become instant.
 *
 * MCS-34's fence, and the reason this is a JavaScript check and not a `@media`
 * rule: CSS can take the transition off a carousel and leave it advancing every six
 * seconds, which is the same vestibular problem with the animation removed. So the
 * timer is never started when the setting is on, and one already running is stopped
 * the moment the setting changes — `useReducedMotion` subscribes for exactly that.
 *
 * Autoplay also suspends on hover and on focus-within, so a reader reaching for a
 * link inside a slide is not racing it.
 *
 * ### `scrollTo` on the track, not `scrollIntoView` on the slide
 *
 * `scrollIntoView` scrolls *every* ancestor scroller, including the document — so
 * an autoplay advance on a carousel that is half off-screen yanks the page under a
 * reader who is looking at something else. `scrollTo` on the track cannot move
 * anything but the track. `behavior` is passed explicitly rather than set in CSS,
 * because a CSS `scroll-behavior: smooth` would also apply to the keyboard and dot
 * controls under reduced motion, where the requirement is that they jump.
 */
/**
 * Every string the carousel renders, resolved on the server (MCS-36 D3).
 *
 * `announcements` is one live-region sentence **per slide**, pre-resolved. It reads
 * like a runtime string — it embeds the slide's index, its count and, where the
 * caller supplied slide names, the headline — but all three are known at build, so
 * the whole set is produced there and the client picks one. That is what removes the
 * last reason this component needed a translator.
 */
export interface HeroCarouselLabels {
  /** The carousel's accessible name — the page's own, not this component's. */
  readonly label: string;
  readonly roleDescription: string;
  readonly slideRoleDescription: string;
  readonly pause: string;
  readonly play: string;
  /** One "go to slide N" name per slide. */
  readonly goToSlide: readonly string[];
  /** One `aria-label` per slide, for the slide group itself. */
  readonly slidePosition: readonly string[];
  /** One live-region announcement per slide. */
  readonly announcements: readonly string[];
}

export function HeroCarousel({
  labels,
  slides,
  className,
  slideClassName,
}: {
  readonly labels: HeroCarouselLabels;
  readonly slides: readonly ReactNode[];
  readonly className?: string;
  readonly slideClassName?: string;
}) {
  const trackId = useId();
  const trackRef = useRef<HTMLDivElement>(null);
  const slideRefs = useRef<(HTMLDivElement | null)[]>([]);
  const dotRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const activeRef = useRef(0);
  const [active, setActive] = useState(0);
  const [paused, setPaused] = useState(false);
  const [suspended, setSuspended] = useState(false);
  const [hidden, setHidden] = useState(false);
  /**
   * What the live region says, and **empty for an autoplay advance**.
   *
   * APG asks for a polite announcement when the reader moves the carousel; it
   * asks just as clearly for silence when the carousel moves itself. A live region
   * that narrated every autoplay tick would interrupt a screen reader every six
   * seconds, which turns the one affordance added for those readers into the
   * reason they leave the page. So the announcement is written by the *controls*
   * — dots and arrow keys — and never by the timer.
   */
  const [announcement, setAnnouncement] = useState('');
  const reduced = useReducedMotion();
  const count = slides.length;

  /*
   * Derived, not mirrored. `playing` is a function of three things — the reader's
   * pause button, the reduced-motion setting and whether there is more than one
   * slide — so holding it in state would mean an effect writing it back on every
   * change, which is a cascading render and a second source of truth for the same
   * fact. `paused` is the only thing here that is genuinely state: it is the one
   * a reader sets.
   */
  const playing = !paused && !reduced && count > 1;

  /**
   * `announce` is what separates a user-driven move from an autoplay one, and it
   * is a parameter rather than a heuristic because every other way of telling
   * them apart is a guess about timing.
   */
  const goTo = useCallback(
    (index: number, announce = false) => {
      const track = trackRef.current;
      const slide = slideRefs.current[index];
      if (!track || !slide) return;

      track.scrollTo({
        left: slide.offsetLeft - track.offsetLeft,
        behavior: prefersReducedMotion() ? 'auto' : 'smooth',
      });

      if (!announce) return;

      setAnnouncement(labels.announcements[index] ?? '');
    },
    [labels.announcements],
  );

  // Which slide is showing. Driven by the scroller rather than by the control that
  // was pressed, so a swipe, a keypress, a dot and the timer all agree.
  useEffect(() => {
    const track = trackRef.current;
    if (!track) return;

    const stops = slideRefs.current.map((element, index) =>
      element
        ? observeIntersection(
            element,
            (entry) => {
              if (!entry.isIntersecting) return;
              activeRef.current = index;
              setActive(index);
            },
            { root: track, threshold: 0.6 },
          )
        : () => {},
    );

    return () => {
      for (const stop of stops) stop();
    };
  }, [count]);

  /*
   * A backgrounded tab must not advance.
   *
   * `setInterval` is throttled in a hidden tab but not stopped, so a reader who
   * comes back after five minutes finds the carousel somewhere they did not leave
   * it — and, on a phone, has paid for the wake-ups. `visibilitychange` is the
   * only signal that covers tab switches, window minimisation and the phone
   * screen going off; a `blur` listener catches none of the three reliably.
   */
  useEffect(() => {
    if (typeof document === 'undefined') return;

    const sync = () => setHidden(document.hidden);
    sync();
    document.addEventListener('visibilitychange', sync);
    return () => document.removeEventListener('visibilitychange', sync);
  }, []);

  useEffect(() => {
    if (!playing || suspended || hidden) return;

    /*
     * The media query is read again here, live, and not only through `reduced`.
     *
     * `useReducedMotion` has to start `false` — the server has no `matchMedia`, so
     * any other initial value is a hydration mismatch — which means there is
     * exactly one commit, the first, where `reduced` is still `false` on a machine
     * that has the setting on. A timer registered in that commit would be cleared
     * a microtask later and would never fire; but "never fires" is a timing
     * argument, and MCS-34's fence is that autoplay does not start. So it does not
     * start.
     */
    if (prefersReducedMotion()) return;

    const timer = window.setInterval(() => {
      // No `announce` argument: an autoplay advance is silent to the live region.
      goTo((activeRef.current + 1) % count);
    }, AUTOPLAY_INTERVAL_MS);

    return () => window.clearInterval(timer);
  }, [playing, suspended, hidden, count, goTo]);

  /*
   * The progress fill on the active dot.
   *
   * `Element.animate()` over the registered `--mr-www-carousel-progress`, so the
   * tween is the compositor's and the main thread does nothing per frame. It is
   * re-armed whenever the active slide changes or the run/pause state does, which
   * is exactly when the fill should restart or stop.
   *
   * Cancelled — not left at its end value — when autoplay is not running, so the
   * property falls back to its `initial-value: 1` and the dot reads as a solid
   * "you are here" rather than as a progress bar frozen mid-sweep.
   */
  useEffect(() => {
    const dot = dotRefs.current[active];
    if (!dot || typeof dot.animate !== 'function') return;

    if (!playing || suspended || hidden) return;

    const animation = dot.animate(
      // Typed custom properties are animatable by WAAPI in engines that support
      // `@property`; where they are not, this is a no-op and the dot stays solid.
      { '--mr-www-carousel-progress': [0, 1] } as unknown as Keyframe[],
      { duration: AUTOPLAY_INTERVAL_MS, easing: 'linear', fill: 'none' },
    );

    return () => animation.cancel();
  }, [active, playing, suspended, hidden]);

  return (
    <section
      aria-label={labels.label}
      aria-roledescription={labels.roleDescription}
      className={cx('relative', className)}
      onMouseEnter={() => setSuspended(true)}
      onMouseLeave={() => setSuspended(false)}
      onFocusCapture={() => setSuspended(true)}
      onBlurCapture={() => setSuspended(false)}
    >
      {/*
        The APG "scrollable region" pattern, in full: `role="region"`, an accessible
        name, and `tabIndex={0}` so the region is reachable. Without the tab stop the
        only way to see slide three is a mouse or a touchscreen, which fails WCAG
        2.1.1 on the one control this component exists to provide.

        The two rules disabled below are heuristics for a case this is not. Both
        assume a non-interactive element that has been given behaviour it should not
        have; a *scroll container* is interactive to a keyboard by construction, and
        the arrow-key handler is what makes it snap to the next slide instead of
        nudging a few pixels and being pulled back by `scroll-snap-type: mandatory`.
        Scoped to these two lines, with the dots — real `<button>`s — as the
        redundant path for anyone the region does not suit.
      */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions */}
      <div
        id={trackId}
        ref={trackRef}
        className="mr-carousel-track"
        role="region"
        aria-label={labels.label}
        // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
        tabIndex={0}
        onKeyDown={(event) => {
          if (event.key === 'ArrowRight') {
            event.preventDefault();
            goTo(Math.min(activeRef.current + 1, count - 1), true);
          } else if (event.key === 'ArrowLeft') {
            event.preventDefault();
            goTo(Math.max(activeRef.current - 1, 0), true);
          }
        }}
      >
        {slides.map((slide, index) => (
          <div
            key={index}
            ref={(element) => {
              slideRefs.current[index] = element;
            }}
            className={cx('mr-carousel-slide', slideClassName)}
            role="group"
            aria-roledescription={labels.slideRoleDescription}
            aria-label={labels.slidePosition[index] ?? ''}
          >
            {slide}
          </div>
        ))}
      </div>

      {/*
        The control row. **The hit areas are deliberately bigger than the marks
        inside them, and that is an S19 fix rather than a style change.**

        8x8 is the right *visual* weight for a carousel indicator and the wrong
        target. axe reported every inactive dot against SC 2.5.8 twice over — once
        for size and once for the 24px undisturbed circle, which at `gap-sm` had a
        diameter of 16px. So the dot stopped being the button: it is now a `<span>`
        the button centres, and the button is 44px tall and 32px wide. Dot centres
        land 32px apart, the circles clear each other, and the row looks exactly as
        it did.

        The dots sit in their own `gap-0` group for the same reason — the space
        between them is now *inside* the buttons, where it counts as target, rather
        than between them where it counts as nothing.
      */}
      <div className="mt-lg flex items-center justify-center gap-sm">
        <button
          type="button"
          aria-controls={trackId}
          aria-label={playing ? labels.pause : labels.play}
          onClick={() => setPaused((wasPaused) => !wasPaused)}
          className="grid size-11 place-items-center rounded-sm text-on-surface-variant hover:text-on-surface"
        >
          {/*
            Two glyphs rather than an icon dependency: `⏸`/`▶` are text, so they
            take the surrounding colour and scale with the reader's font size.
            `aria-hidden`, because the button's name is the resource above.
          */}
          <span aria-hidden className="text-body-sm leading-none">
            {playing ? '⏸' : '▶'}
          </span>
        </button>

        <div className="flex items-center gap-0">
          {slides.map((_, index) => (
            <button
              key={index}
              type="button"
              ref={(element) => {
                dotRefs.current[index] = element;
              }}
              aria-controls={trackId}
              aria-current={active === index ? 'true' : undefined}
              aria-label={labels.goToSlide[index] ?? ''}
              onClick={() => goTo(index, true)}
              className="grid h-11 w-8 place-items-center rounded-sm"
            >
              {/*
                The mark. `mr-carousel-dot` and `data-mr-active` moved down here
                together, so every rule in `globals.css` still matches the element
                it was written for.

                The WAAPI progress animation is unaffected and this is worth being
                explicit about: it animates `--mr-www-carousel-progress` on the
                *button* (that is what `dotRefs` holds), and a custom property
                inherits — so the `::after` fill on this span reads the same value
                from one level up that it used to read from itself.
              */}
              <span
                aria-hidden
                data-mr-active={active === index ? 'true' : 'false'}
                className={cx(
                  'mr-carousel-dot h-2 rounded-sm',
                  // The active dot is the progress *track*; `::after` is the fill and
                  // is drawn in `primary` over this. An inactive dot has no fill.
                  active === index ? 'w-6 bg-outline-variant' : 'w-2 bg-outline',
                )}
              />
            </button>
          ))}
        </div>
      </div>

      {/*
        The live region. Empty on an autoplay advance — see `announcement`.

        `aria-live="polite"` with `aria-atomic`, outside the track so that moving
        the carousel does not also move the node the reader is being read. It is
        `sr-only` rather than hidden: `display: none` removes a live region from
        the accessibility tree and it announces nothing at all, which is the usual
        way this control ships broken.
      */}
      <p aria-live="polite" aria-atomic="true" className="sr-only">
        {announcement}
      </p>
    </section>
  );
}
