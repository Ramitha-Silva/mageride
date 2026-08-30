import { FaqAccordion } from '@/components/faq/FaqAccordion';
import { FaqHashOpener } from '@/components/faq/FaqHashOpener';
import { JsonLd } from '@/components/seo/JsonLd';
import { PAGES } from '@/content/pages';
import { createWwwTranslator } from '@/i18n';
import { faqPage } from '@/lib/json-ld';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { metadataForRoute } from '@/lib/seo';

/**
 * `/{locale}/faq` — twenty-one questions, grouped by audience.
 *
 * The accordion is a **server component** and every answer is in the markup whether
 * its item is open or closed, which is three obligations rather than one: a crawler
 * indexes them, a reader with no JavaScript can open any of them, and the `FAQPage`
 * JSON-LD S19 emits will describe content that is genuinely on the page rather than
 * markup asserting answers a visitor cannot see. Google treats the second as
 * structured-data spam, and it would be right to.
 *
 * `FaqHashOpener` is the page's only script: it opens the item a `#faq-<id>` link
 * names. Nothing else here hydrates.
 *
 * The `FAQPage` block (S19) reads the **same `FAQ` array** the accordion renders, so
 * the structured data and the visible page are the same twenty-one entries by
 * construction rather than by review.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'faq');
}

export default async function FaqPage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);
  const t = createWwwTranslator(locale);
  const copy = PAGES.faq;

  return (
    <div className="mx-auto flex max-w-[1200px] flex-col gap-section px-4 py-section">
      <header className="flex flex-col gap-md">
        <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
          {t(copy?.title ?? 'www.page.faq.title')}
        </h1>
        <p className="max-w-[62ch] text-body text-on-surface-variant">
          {t(copy?.intro ?? 'www.page.faq.intro')}
        </p>
      </header>

      <FaqAccordion locale={locale} />
      <FaqHashOpener />
      <JsonLd nodes={[faqPage(locale)]} />
    </div>
  );
}
