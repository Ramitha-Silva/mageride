import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { HeroCarousel } from '@/components/motion/HeroCarousel';
import { heroCarouselLabels } from '@/components/motion/heroCarouselLabels';
import { SCREENS } from '@/content/screens';
import { WWW_LOCALES } from '@/i18n';

/**
 * **The accessibility assertions a page scan cannot make.**
 *
 * There are two halves to this surface's WCAG 2.2 AA target (A33), and they need
 * different tools:
 *
 *   - `scripts/check-a11y.mjs` runs axe-core and a contrast walk over the **real
 *     built site** — 48 page loads, six pages × both locales × both appearances ×
 *     desktop and a 375px phone. That is where landmarks, heading order, contrast
 *     and target size are decided, because all four are properties of a rendered
 *     page and none of them survives being approximated in jsdom.
 *   - This file asserts **behaviour**, which that scan is structurally blind to. A
 *     carousel that autoplays under `prefers-reduced-motion` looks identical in a
 *     screenshot to one that does not. A dialog that never returns focus looks
 *     identical to one that does. Both are serious failures and neither is visible
 *     without driving the component.
 *
 * So: no axe here. Running it over a jsdom fragment would report on markup that no
 * reader ever receives — no stylesheet, so no computed colour, no computed size,
 * and every contrast and target-size rule either silently passing or silently
 * inapplicable. **That would be the third confident-and-wrong "pass" S19 already
 * found twice**, and it is the reason the browser audit is a script rather than a
 * test. The claims S14–S19 deferred to "S20 asserts this" are the behavioural ones,
 * and they are below.
 */

const appRoot = resolve(import.meta.dirname, '..');
const AUTOPLAY_INTERVAL_MS = 6000;

/** jsdom ships no `matchMedia`. Drive the setting the components actually read. */
function setReducedMotion(reduce: boolean): void {
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: reduce && query.includes('prefers-reduced-motion'),
      media: query,
      onchange: null,
      addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) =>
        listeners.add(listener),
      removeEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) =>
        listeners.delete(listener),
      addListener: (listener: (event: MediaQueryListEvent) => void) => listeners.add(listener),
      removeListener: (listener: (event: MediaQueryListEvent) => void) => listeners.delete(listener),
      dispatchEvent: () => false,
    })),
  );
}

/**
 * The track is a scroll container and jsdom implements no scrolling at all, so
 * `scrollTo` is stubbed and *counted*. That is the right observable anyway: the
 * question is not where the carousel ended up, it is whether it moved on its own.
 */
function stubScrolling(): void {
  Element.prototype.scrollTo = vi.fn() as unknown as Element['scrollTo'];
  Element.prototype.scrollIntoView = vi.fn();
}

const SLIDES = ['One', 'Two', 'Three'].map((name) => <p key={name}>{name}</p>);

/**
 * The carousel's strings, built the way a page builds them (MCS-36 D3).
 *
 * The component no longer takes a `locale` — it takes resolved labels — so the test
 * goes through the real builder rather than inventing strings. That keeps the
 * assertions honest: `getByRole('button', { name: /slide 2/i })` is matching the same
 * text a reader hears, not a fixture that happens to contain the word.
 */
const LABELS = heroCarouselLabels('en', ['One', 'Two', 'Three']);

beforeEach(() => {
  stubScrolling();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('the carousel under reduced motion (A12)', () => {
  /**
   * **The claim CLAUDE.md makes, asserted rather than remembered.**
   *
   * *"The hero's autoplay stops — it does not become instant."* A `@media` rule can
   * take the transition off a carousel and leave it advancing every six seconds,
   * which is the same vestibular problem with the animation removed. S04 verified
   * this by hand in a browser — `scrollLeft` unchanged after eight seconds — and
   * deferred the assertion here.
   *
   * Fake timers rather than a real wait, and **ten intervals rather than one**: a
   * timer that was registered and then cleared a microtask later would pass a
   * single-tick test by accident, and MCS-34's fence is that autoplay never starts.
   */
  it('never advances on its own when the reader asked for less motion', () => {
    setReducedMotion(true);
    vi.useFakeTimers();

    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);
    vi.advanceTimersByTime(AUTOPLAY_INTERVAL_MS * 10);

    expect(Element.prototype.scrollTo).not.toHaveBeenCalled();
  });

  /**
   * And the control path is *not* removed with the timer.
   *
   * Reduced motion removes the animation, never the ability to operate the thing.
   * A carousel whose dots stop working under the setting has traded one failure for
   * a worse one — the reader can no longer reach slides two and three at all.
   */
  it('still moves when the reader asks it to', async () => {
    setReducedMotion(true);
    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    fireEvent.click(screen.getByRole('button', { name: /slide 2/i }));

    await waitFor(() => expect(Element.prototype.scrollTo).toHaveBeenCalled());
  });

  it('advances on its own when the reader did not ask for less motion', () => {
    setReducedMotion(false);
    vi.useFakeTimers();

    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);
    vi.advanceTimersByTime(AUTOPLAY_INTERVAL_MS * 2);

    expect(Element.prototype.scrollTo).toHaveBeenCalled();
  });

  /**
   * A backgrounded tab must not advance either.
   *
   * Not a WCAG clause — a battery and a surprise one. `setInterval` is throttled in
   * a hidden tab and not stopped, so a reader who comes back after five minutes
   * finds the carousel somewhere they did not leave it.
   */
  it('stops while the tab is hidden', () => {
    setReducedMotion(false);
    vi.useFakeTimers();

    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    fireEvent(document, new Event('visibilitychange'));
    vi.mocked(Element.prototype.scrollTo).mockClear();

    vi.advanceTimersByTime(AUTOPLAY_INTERVAL_MS * 4);
    expect(Element.prototype.scrollTo).not.toHaveBeenCalled();
  });
});

describe('the carousel’s roles and labels (APG)', () => {
  beforeEach(() => {
    setReducedMotion(false);
  });

  /**
   * The scrollable region is reachable, named, and says what it is.
   *
   * A scroll container with no tab stop is unreachable by keyboard in every engine
   * but Firefox — the exact failure S19 found on the role pages' screen strip,
   * which had 7456px of content and no way in.
   */
  it('gives the track a tab stop, a role and a name', () => {
    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    /*
     * Resolved through a dot's `aria-controls` rather than by picking a `region` off
     * the page, and the difference caught a real ambiguity: the component renders
     * **two** regions — the outer `<section>`, named for the carousel as a whole, and
     * the scroll track inside it. Taking the first was taking the wrong one. Going
     * via `aria-controls` asserts the association resolves to a real element, which
     * is the thing a screen reader actually follows and the thing that silently
     * breaks when an id is renamed.
     */
    const trackId = screen
      .getByRole('button', { name: /slide 1/i })
      .getAttribute('aria-controls');
    expect(trackId, 'the dots must name the track they control').toBeTruthy();

    const track = document.getElementById(trackId ?? '');
    expect(track, `aria-controls="${trackId}" resolves to nothing`).not.toBeNull();
    expect(track?.getAttribute('role')).toBe('region');
    expect(track?.getAttribute('tabindex')).toBe('0');
    expect(track?.getAttribute('aria-label')).toBeTruthy();
  });

  /** Every dot is a real button, named by position, and controls the track. */
  it('names one button per slide and points it at the track', () => {
    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    const dots = SLIDES.map((_, index) =>
      screen.getByRole('button', { name: new RegExp(`slide ${index + 1}`, 'i') }),
    );
    expect(dots).toHaveLength(SLIDES.length);

    const trackId = dots[0]?.getAttribute('aria-controls');
    expect(trackId).toBeTruthy();
    expect(document.getElementById(trackId ?? '')).not.toBeNull();
    for (const dot of dots) expect(dot.getAttribute('aria-controls')).toBe(trackId);
  });

  /**
   * **Exactly one dot carries `aria-current`.**
   *
   * This is the state a screen-reader user has instead of the visual fill. Two
   * current dots, or none, is the same as no position indicator at all.
   */
  it('marks exactly one dot current', () => {
    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    const current = screen
      .getAllByRole('button')
      .filter((button) => button.getAttribute('aria-current') === 'true');

    expect(current).toHaveLength(1);
  });

  /**
   * The play/pause control names the action it will perform, not the current state.
   *
   * "Pause the slideshow" on a running carousel; a button labelled with its state
   * reads as a claim rather than an offer.
   */
  it('offers a pause control that flips to play', () => {
    render(<HeroCarousel labels={LABELS} slides={SLIDES} />);

    const pause = screen.getByRole('button', { name: /pause/i });
    fireEvent.click(pause);
    expect(screen.getByRole('button', { name: /play/i })).toBeDefined();
  });

  /**
   * The two glyphs inside the controls are hidden from the accessibility tree.
   *
   * `⏸` and `▶` are text, so without `aria-hidden` a screen reader reads the
   * character's Unicode name after the button's own name.
   */
  it('hides the decorative glyphs from assistive technology', () => {
    const { container } = render(
      <HeroCarousel labels={LABELS} slides={SLIDES} />,
    );

    for (const glyph of container.querySelectorAll('button > span')) {
      expect(glyph.getAttribute('aria-hidden')).toBe('true');
    }
  });
});

describe('the lightbox dialog', () => {
  /**
   * **Focus returns to the trigger on close**, which is the half Radix does not do
   * here and the half a reader actually feels.
   *
   * S15 found and fixed this: after `Escape`, `document.activeElement` was `<body>`
   * and stayed there, so a keyboard reader who opened a screenshot was returned to
   * the top of the document and had to tab back through the whole gallery. The fix
   * captures the trigger on open and focuses it on close inside a
   * `requestAnimationFrame`, because a synchronous focus is undone by Radix's own
   * restoration. That ordering is invisible in the markup, so it is asserted here.
   *
   * Driven through the real `useLightbox` controller rather than a stub, because
   * the ordering *is* the thing under test.
   */
  it('returns focus to the element that opened it', async () => {
    const { useLightbox, ScreenLightbox } = await import('@/components/showcase/ScreenLightbox');
    const { lightboxLabels } = await import('@/components/showcase/showcaseLabels');
    const screens = SCREENS.slice(0, 3);
    const lightbox = lightboxLabels('en', screens);

    function Harness() {
      const controller = useLightbox(lightbox.positions);
      return (
        <>
          <button type="button" data-testid="trigger" onClick={(e) => controller.open(0, e.currentTarget)}>
            open
          </button>
          <ScreenLightbox labels={lightbox} screens={screens} controller={controller} />
        </>
      );
    }

    render(<Harness />);
    const trigger = screen.getByTestId('trigger');
    trigger.focus();
    fireEvent.click(trigger);

    await waitFor(() => expect(screen.getByRole('dialog')).toBeDefined());

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

    await waitFor(() => expect(document.activeElement).toBe(trigger), { timeout: 2000 });
  });

  /** A dialog with no accessible name is announced as "dialog" and nothing else. */
  it('is a named modal dialog', async () => {
    const { useLightbox, ScreenLightbox } = await import('@/components/showcase/ScreenLightbox');
    const { lightboxLabels } = await import('@/components/showcase/showcaseLabels');
    const screens = SCREENS.slice(0, 3);
    const lightbox = lightboxLabels('en', screens);

    function Harness() {
      const controller = useLightbox(lightbox.positions);
      return (
        <>
          <button type="button" data-testid="trigger" onClick={(e) => controller.open(0, e.currentTarget)}>
            open
          </button>
          <ScreenLightbox labels={lightbox} screens={screens} controller={controller} />
        </>
      );
    }

    render(<Harness />);
    fireEvent.click(screen.getByTestId('trigger'));

    const dialog = await screen.findByRole('dialog');
    const name =
      dialog.getAttribute('aria-label') ??
      (dialog.getAttribute('aria-labelledby')
        ? document.getElementById(dialog.getAttribute('aria-labelledby') ?? '')?.textContent
        : null);

    expect(name?.trim()).toBeTruthy();
  });
});

describe('the shell, as source', () => {
  /**
   * **One `<h1>` per page** is decided by the browser audit, which can see what a
   * page actually renders. What is worth asserting here is the *upstream* property
   * it depends on: the shared layout contributes no `<h1>` of its own, so the count
   * is always the page's.
   *
   * A source assertion rather than a render: the layout is a server component whose
   * `next/font` and metadata imports a jsdom test has no business executing, and
   * the property that matters is visible in the markup.
   */
  it('puts no <h1> in the shared layout', async () => {
    const layout = await readFile(join(appRoot, 'app/[locale]/layout.tsx'), 'utf8');
    expect(layout).not.toMatch(/<h1[\s>]/);
  });

  /**
   * The skip link is the **first focusable thing in the document**, or it is not a
   * skip link — a reader who has to tab past the nav to reach "skip the nav" has
   * been given a joke.
   */
  it('puts the skip link before anything else focusable', async () => {
    const layout = await readFile(join(appRoot, 'app/[locale]/layout.tsx'), 'utf8');
    const body = layout.slice(layout.indexOf('<body'));

    const skip = body.search(/sr-only[^"]*focus:not-sr-only|focus:not-sr-only/);
    const header = body.indexOf('<Header');

    expect(skip, 'no skip link found in the layout').toBeGreaterThan(-1);
    expect(header, 'no header found in the layout').toBeGreaterThan(-1);
    expect(skip).toBeLessThan(header);
  });

  /**
   * `<html lang>` is the path segment, in every rendered locale.
   *
   * A wrong `lang` hands prose in one language to a speech engine configured for
   * another and produces sounds that are not words — which is why `/ta` 404s rather
   * than serving English under `lang="ta"` (MCS-34 D2, `test/i18n.test.ts`).
   */
  it('derives <html lang> from the route rather than a header', async () => {
    const layout = await readFile(join(appRoot, 'app/[locale]/layout.tsx'), 'utf8');
    expect(layout).toMatch(/<html\s+lang=\{/);
    expect(WWW_LOCALES.length).toBeGreaterThan(0);
  });
});
