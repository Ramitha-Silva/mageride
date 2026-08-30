import { ImageResponse } from 'next/og';

import { OgCard, OG_CONTENT_TYPE, OG_SIZE } from '@/components/seo/OgCard';
import { WWW_LOCALES } from '@/i18n';
import { LEGAL_DOCS, ROUTE_BY_PATH, type LegalDoc } from '@/lib/routes';
import { createWwwTranslator, FALLBACK_LOCALE } from '@/i18n';

/**
 * The legal-document card family.
 *
 * English in every locale — `src/components/seo/OgCard.tsx` records why, and it
 * bites least here: these three documents have English-only names in every register
 * anybody links them by, and the card's job is to say *which document* a link opens.
 *
 * The title is the route's nav label — the same key the page renders as its `<h1>`
 * — read through the English row. So a fourth legal document gets a card with no
 * edit here, and no card can name a document by a title the page does not use.
 */
export const size = OG_SIZE;
export const contentType = OG_CONTENT_TYPE;
const t = createWwwTranslator(FALLBACK_LOCALE);

export const alt = `${t('www.brand.name')} — ${t('www.footer.legal')}`;

export function generateStaticParams(): { locale: string; doc: LegalDoc }[] {
  return WWW_LOCALES.flatMap((locale) => LEGAL_DOCS.map((doc) => ({ locale, doc })));
}

export const dynamicParams = false;

export default async function Image({ params }: { params: Promise<{ doc: string }> }) {
  const { doc } = await params;
  const label = ROUTE_BY_PATH[`legal/${doc as LegalDoc}`]?.labelKey;

  return new ImageResponse(
    (
      <OgCard
        brand={t('www.brand.name')}
        eyebrow={t('www.footer.legal')}
        title={t(label ?? 'www.footer.legal')}
      />
    ),
    size,
  );
}
