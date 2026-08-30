import Link from 'next/link';

import { chaptersFor } from '@/content/index';
import { PAGES } from '@/content/pages';
import { createWwwTranslator } from '@/i18n';
import { GUIDE_AUDIENCES, href } from '@/lib/routes';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { metadataForRoute } from '@/lib/seo';

/**
 * `/{locale}/guide` — the index over both guides.
 *
 * Two audience sections, chapters in `order`, each card carrying its title, its
 * summary and **how many steps it has**. S17 gives the reason for that last one and
 * it is a reader's reason rather than a completeness one: *"a reader deciding
 * whether to open 'Onboarding your vehicle' wants to know it is nine steps before
 * they start."*
 *
 * The headings come from `PAGES.guide.sections` — the passenger and driver headings
 * S07 wrote — so the index does not invent names for the two guides that the rest
 * of the site does not use.
 *
 * Everything is read from `chaptersFor()`, which sorts by `Chapter.order`. There is
 * no list of 34 chapters in this file and there must never be one: the registry is
 * the only path to a chapter, which is what makes "a chapter that exists and does
 * not appear here is impossible" true rather than merely intended.
 */
export async function generateMetadata({ params }: { params: Promise<LocaleParams> }) {
  return metadataForRoute(await localeFrom(params), 'guide');
}

export default async function GuideIndexPage({ params }: { params: Promise<LocaleParams> }) {
  const locale = await localeFrom(params);
  const t = createWwwTranslator(locale);
  const copy = PAGES.guide;

  return (
    <div className="mx-auto max-w-[1200px] px-4 py-section">
      <h1 className="max-w-[20ch] font-display text-hero text-balance text-on-surface">
        {t(copy?.title ?? 'www.page.guide.title')}
      </h1>
      <p className="mt-md max-w-[62ch] text-body text-on-surface-variant">
        {t(copy?.intro ?? 'www.page.guide.intro')}
      </p>

      {GUIDE_AUDIENCES.map((audience, index) => {
        const chapters = chaptersFor(audience);
        const heading = copy?.sections[index]?.heading;

        return (
          <section key={audience} className="mt-section">
            <div className="flex flex-wrap items-baseline justify-between gap-sm">
              <h2 className="font-display text-hero-sm text-on-surface">
                {t(heading ?? 'www.page.guide.passengerHeading')}
              </h2>
              <p className="text-body-sm text-on-surface-variant">
                {t('www.page.guide.chapterCount', { count: chapters.length })}
              </p>
            </div>

            <ol className="mt-lg grid gap-md md:grid-cols-2 lg:grid-cols-3">
              {chapters.map((chapter) => (
                <li key={chapter.id} className="flex">
                  {/*
                    The whole card is the link, so the tap target is the card and
                    not a four-word title — this list is read on a phone by
                    somebody holding a steering wheel five minutes ago.
                  */}
                  <Link
                    href={href(locale, `guide/${chapter.audience}/${chapter.slug}`)}
                    className="flex flex-1 flex-col gap-xs rounded-card border border-outline-variant p-lg transition-colors hover:bg-surface-variant focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary"
                  >
                    <span className="text-body-sm font-medium text-secondary">
                      {t('www.guide.chapterNumber', { number: chapter.order })}
                    </span>
                    <span className="font-display text-title text-on-surface">
                      {t(chapter.title)}
                    </span>
                    <span className="text-body-sm text-on-surface-variant">
                      {t(chapter.summary)}
                    </span>
                    <span className="mt-auto pt-sm text-body-sm font-medium text-on-surface-variant">
                      {t('www.guide.stepCount', { count: chapter.steps.length })}
                    </span>
                  </Link>
                </li>
              ))}
            </ol>
          </section>
        );
      })}
    </div>
  );
}
