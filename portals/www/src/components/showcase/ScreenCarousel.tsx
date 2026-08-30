'use client';

import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import type { ScreenEntry } from '@/content/screens';

import { ScreenLightbox, useLightbox, type LightboxLabels } from './ScreenLightbox';

/**
 * Section 6 of the home page — a snap carousel of thumbnails that open into the
 * lightbox.
 *
 * **The lightbox is `./ScreenLightbox`**, extracted in S17 so that the guide's
 * inline screen references and this carousel share one implementation. S17's fence:
 * *"One lightbox implementation across the whole site."* What lives here is the
 * strip; the dialog, its keyboard handling, its live region and its focus
 * restoration all live there and were measured once.
 *
 * `mr-carousel-track` is S04's scroll-snap utility, reused rather than redefined:
 * swipe, momentum and snapping come from the platform's own scroller. This strip
 * has no timer, no observer and no dots — it is a scroller, and a scroller is
 * already keyboard-operable.
 */
/** The strip's two strings, its per-thumbnail names, and the dialog's (MCS-36 D3). */
export interface ScreenCarouselLabels {
  readonly heading: string;
  /** The scroller's accessible name. */
  readonly stripLabel: string;
  /** One "open this screen" name per thumbnail, index-aligned with `screens`. */
  readonly openLabels: readonly string[];
  readonly lightbox: LightboxLabels;
}

export function ScreenCarousel({
  labels,
  screens,
}: {
  readonly labels: ScreenCarouselLabels;
  readonly screens: readonly ScreenEntry[];
}) {
  const lightbox = useLightbox(labels.lightbox.positions);

  return (
    <section className="py-section">
      <div className="mx-auto max-w-[1200px] px-4">
        <h2 className="font-display text-hero-sm text-on-surface">{labels.heading}</h2>
      </div>

      {/*
        The strip bleeds past the 1200px cap on purpose — a horizontal scroller
        that stops at the container looks like it has ended. `px-4` on the list
        keeps the first and last thumbnails off the screen edge.
      */}
      <ul
        aria-label={labels.stripLabel}
        className="mr-carousel-track mt-lg gap-md px-4 pb-sm"
      >
        {screens.map((screen, index) => (
          <li key={screen.id} className="shrink-0 snap-start">
            <button
              type="button"
              aria-label={labels.openLabels[index] ?? ''}
              onClick={(event) => lightbox.open(index, event.currentTarget)}
              className={cx(
                'block rounded-card',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
              )}
            >
              <ScreenImage
                screen={screen}
                alt={labels.lightbox.captions[index] ?? ''}
                sizes="14rem"
                className="w-[14rem]"
              />
            </button>
          </li>
        ))}
      </ul>

      <ScreenLightbox labels={labels.lightbox} screens={screens} controller={lightbox} />
    </section>
  );
}
