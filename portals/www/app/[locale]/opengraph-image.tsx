import { ImageResponse } from 'next/og';

import { OgCard, OG_CONTENT_TYPE, OG_SIZE } from '@/components/seo/OgCard';
import { ogLocaleParams } from '@/components/seo/routeOgImage';
import { createWwwTranslator, FALLBACK_LOCALE } from '@/i18n';

/**
 * The site card — `/{locale}` and everything below it that does not override.
 *
 * Next resolves `opengraph-image` per segment and the nearest one wins, so this
 * covers the home page, `/screens`, `/faq`, `/download` and `/contact`; the four
 * role pages, the guide chapters and the legal documents have their own beside
 * their routes. That is S19's four families expressed the only way the App Router
 * allows — an `opengraph-image` sees its own segment's params and nothing else, so
 * "one card per family" means one file per segment that has a family.
 *
 * **English in every locale, deliberately.** `src/components/seo/OgCard.tsx` records
 * the finding: satori takes TTF/OTF/WOFF, every font file this repository can reach
 * is WOFF2, and there is no Noto Sans Sinhala on this host in any format. The
 * alternative to an English card is not a Sinhala card — it is a row of empty boxes
 * where the title should be. The strings are still read from `en.ts` rather than
 * typed here.
 *
 * `alt` is what a reader with images off is given, so it says what the picture
 * says — and it is resolved from the same two keys.
 */
const t = createWwwTranslator(FALLBACK_LOCALE);

export const size = OG_SIZE;
export const contentType = OG_CONTENT_TYPE;
export const alt = `${t('www.brand.name')} — ${t('www.brand.tagline')}`;

/**
 * **Prerendered, not rendered on demand.**
 *
 * Without this the route builds as `ƒ` — dynamic — and every crawler fetching a
 * link preview would run satori and a WASM rasteriser on the server. That is a
 * request-time code path on the surface whose defining property is that it has
 * none, and it is the expensive kind: an OG image is fetched by every social
 * platform, every chat client and every preview bot that sees the link.
 *
 * `ogLocaleParams` reads `WWW_LOCALES`, like every other `generateStaticParams` on
 * this surface — `/ta` is not published, so it gets no card either.
 */
export const generateStaticParams = ogLocaleParams;
export const dynamicParams = false;

export default async function Image() {
  return new ImageResponse(
    (
      <OgCard
        brand={t('www.brand.name')}
        eyebrow={t('www.brand.name')}
        title={t('www.brand.tagline')}
        tagline={t('www.home.modes.heading')}
      />
    ),
    size,
  );
}
