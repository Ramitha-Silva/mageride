import { HOME } from '@/content/pages';
import type { ScreenEntry } from '@/content/screens';
import { createWwwTranslator, type Locale } from '@/i18n';

import type { ScreenCarouselLabels } from './ScreenCarousel';
import type { LightboxLabels } from './ScreenLightbox';

/**
 * The lightbox's strings for a given list of screens, resolved on the server.
 *
 * Both arrays are **indexed by slide** and both are resolved eagerly, including the
 * `"Screen 3 of 12"` announcements — the count is known here, so every one of them
 * can be produced now and picked by index later. That is what lets the dialog run
 * with no translator and no placeholder substitution on the client (MCS-36 D3).
 */
export function lightboxLabels(locale: Locale, screens: readonly ScreenEntry[]): LightboxLabels {
  const t = createWwwTranslator(locale);

  return {
    title: t('www.showcase.lightbox.title'),
    close: t('modal.close'),
    previous: t('www.common.previous'),
    next: t('www.common.next'),
    positions: screens.map((_, index) =>
      t('www.showcase.lightbox.position', { index: index + 1, count: screens.length }),
    ),
    captions: screens.map((screen) => t(screen.captionKey)),
  };
}

/** The strip's own strings, plus the dialog's. */
export function screenCarouselLabels(
  locale: Locale,
  screens: readonly ScreenEntry[],
): ScreenCarouselLabels {
  const t = createWwwTranslator(locale);
  const lightbox = lightboxLabels(locale, screens);

  return {
    heading: t(HOME.screens.heading),
    stripLabel: t('www.showcase.label'),
    // The thumbnail's name embeds its caption, so it is built from the same resolved
    // caption the dialog uses rather than translating the key a second time.
    openLabels: lightbox.captions.map((caption) => t('www.showcase.open', { caption })),
    lightbox,
  };
}
