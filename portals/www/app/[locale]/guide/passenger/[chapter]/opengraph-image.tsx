import { ImageResponse } from 'next/og';

import { OgCard, OG_CONTENT_TYPE, OG_SIZE } from '@/components/seo/OgCard';
import { chapterBySlug } from '@/content/index';
import { createWwwTranslator, FALLBACK_LOCALE, WWW_LOCALES } from '@/i18n';
import { GUIDE_CHAPTERS } from '@/lib/routes';

/**
 * The passenger-guide chapter card — S19's fourth family, one per chapter.
 *
 * **The title is read from the chapter registry**, in English, through the same
 * translator the page uses. `src/components/seo/OgCard.tsx` records why the cards
 * are English and it is a font-availability finding rather than a choice; what
 * matters here is that the string is still *derived* — a chapter renamed in the
 * corpus renames its card, and no card can name a chapter that does not exist.
 *
 * **Prerendered, one per chapter per rendered locale.** Rendering these on demand
 * would put satori and a WASM rasteriser on the path every preview bot walks, on
 * the surface whose defining property is having no request-time work at all.
 */
export const size = OG_SIZE;
export const contentType = OG_CONTENT_TYPE;
const t = createWwwTranslator(FALLBACK_LOCALE);

export const alt = `${t('www.brand.name')} — ${t('www.page.guide.passengerHeading')}`;

export function generateStaticParams(): { locale: string; chapter: string }[] {
  return WWW_LOCALES.flatMap((locale) =>
    GUIDE_CHAPTERS.filter((chapter) => chapter.audience === 'passenger').map((chapter) => ({
      locale,
      chapter: chapter.slug,
    })),
  );
}

export const dynamicParams = false;

export default async function Image({ params }: { params: Promise<{ chapter: string }> }) {
  const { chapter: slug } = await params;
  const chapter = chapterBySlug('passenger', slug);

  return new ImageResponse(
    (
      <OgCard
        brand={t('www.brand.name')}
        eyebrow={t('www.page.guide.passengerHeading')}
        title={chapter ? t(chapter.title) : t('www.nav.guide')}
        tagline={chapter ? t('www.guide.chapterNumber', { number: chapter.order }) : undefined}
      />
    ),
    size,
  );
}
