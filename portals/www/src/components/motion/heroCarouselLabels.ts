import { createWwwTranslator, type Locale } from '@/i18n';

import type { HeroCarouselLabels } from './HeroCarousel';

/**
 * Every string the hero carousel renders, resolved on the server (MCS-36 D3).
 *
 * **`slideNames` is why this takes the headlines rather than deriving them.** The
 * announcement a screen-reader user hears on a user-driven move is the slide's own
 * headline — "Slide 2 of 4: Track every bus" — and the caller is the only thing that
 * knows what its slides say. Where it supplies none, the announcement falls back to
 * the position alone, which is what a carousel of unlabelled images wants.
 *
 * The arrays are one entry per slide, resolved here because the count and the names
 * are both known at build. The client picks by index and holds no translator.
 */
export function heroCarouselLabels(
  locale: Locale,
  slideNames: readonly string[],
): HeroCarouselLabels {
  const t = createWwwTranslator(locale);
  const count = slideNames.length;

  return {
    label: t('www.hero.label'),
    roleDescription: t('www.motion.carousel.roleDescription'),
    slideRoleDescription: t('www.motion.carousel.slideRoleDescription'),
    pause: t('www.motion.carousel.pause'),
    play: t('www.motion.carousel.play'),
    goToSlide: slideNames.map((_, index) =>
      t('www.motion.carousel.goToSlide', { index: index + 1 }),
    ),
    slidePosition: slideNames.map((_, index) =>
      t('www.motion.carousel.slidePosition', { index: index + 1, count }),
    ),
    announcements: slideNames.map((headline, index) =>
      headline
        ? t('www.hero.slideAnnouncement', { index: index + 1, count, headline })
        : t('www.motion.carousel.slidePosition', { index: index + 1, count }),
    ),
  };
}
