import { OG_CONTENT_TYPE, OG_SIZE } from '@/components/seo/OgCard';
import { ogLocaleParams, routeOgImage } from '@/components/seo/routeOgImage';
import { PAGES } from '@/content/pages';
import { createWwwTranslator, FALLBACK_LOCALE } from '@/i18n';

/**
 * The `passengers` card — one of the four role-page cards (S19's third family).
 *
 * Design, strings and the English-only decision all live in
 * `src/components/seo/routeOgImage.tsx` and `OgCard.tsx`; this file is the segment
 * Next resolves the card from, and nothing else. `alt` is resolved from the same
 * `PAGES` entry the card draws, so the two cannot disagree.
 */
const t = createWwwTranslator(FALLBACK_LOCALE);

export const size = OG_SIZE;
export const contentType = OG_CONTENT_TYPE;
export const alt = `${t('www.brand.name')} — ${t(PAGES.passengers?.title ?? 'www.brand.tagline')}`;

export const generateStaticParams = ogLocaleParams;
export const dynamicParams = false;

export default routeOgImage('passengers');
