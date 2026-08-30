import { ImageResponse } from 'next/og';

import { PAGES } from '@/content/pages';
import { createWwwTranslator, FALLBACK_LOCALE, WWW_LOCALES } from '@/i18n';
import { ROUTE_BY_PATH, type RoutePath } from '@/lib/routes';

import { OgCard, OG_SIZE } from './OgCard';

/**
 * A route's Open Graph card, for the four role pages.
 *
 * Each of `/vision`, `/passengers`, `/drivers` and `/fleets` needs its own
 * `opengraph-image` file — the App Router resolves them per segment — and without
 * this they would be four copies of the same twenty lines, which is four places to
 * fix a card design.
 *
 * ## The strings come from `en.ts`, not from a literal
 *
 * `src/components/seo/OgCard.tsx` records why the cards are English: satori takes
 * TTF/OTF/WOFF, every font this repository can reach is WOFF2, and there is no
 * Sinhala face on this host at all. **That is a reason to render the English
 * strings, not a licence to retype them.** `createWwwTranslator(FALLBACK_LOCALE)`
 * reads the same `PAGES` entry and the same nav label the page renders, so a card
 * cannot describe a page by a name the page stopped using.
 *
 * The eyebrow is the route's nav label and the title is its `<h1>` — the two keys
 * `portals/www/CLAUDE.md` already requires a route to bind, used here for the
 * third time and still in agreement.
 */
export function routeOgImage(path: RoutePath) {
  return async function Image() {
    const t = createWwwTranslator(FALLBACK_LOCALE);
    const copy = PAGES[path];

    return new ImageResponse(
      (
        <OgCard
          brand={t('www.brand.name')}
          eyebrow={t(ROUTE_BY_PATH[path].labelKey)}
          title={t(copy?.title ?? ROUTE_BY_PATH[path].labelKey)}
        />
      ),
      OG_SIZE,
    );
  };
}

/**
 * The locales a card is prerendered for.
 *
 * Shared so that no `opengraph-image` route forgets it and builds as `ƒ`. An OG
 * image rendered on demand puts satori and a WASM rasteriser on a request path,
 * on the one surface that has none — and it is a path every social platform, chat
 * client and preview bot walks.
 */
export function ogLocaleParams(): { locale: string }[] {
  return WWW_LOCALES.map((locale) => ({ locale }));
}
