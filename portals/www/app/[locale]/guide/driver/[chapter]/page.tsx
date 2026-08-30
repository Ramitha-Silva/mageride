import { notFound } from 'next/navigation';

import { ChapterPage } from '@/components/guide/ChapterPage';
import { JsonLd } from '@/components/seo/JsonLd';
import { chapterBySlug } from '@/content/index';
import { createWwwTranslator, WWW_LOCALES } from '@/i18n';
import { GUIDE_CHAPTERS } from '@/lib/routes';
import { breadcrumbs, howTo } from '@/lib/json-ld';
import { localeFrom, type LocaleParams } from '@/lib/params';
import { chapterCrumbs, chapterSeo, metadataFor } from '@/lib/seo';

/**
 * A driver guide chapter — `/{locale}/guide/driver/{chapter}`.
 *
 * **The registry is the only path to a chapter.** `generateStaticParams` reads
 * `GUIDE_CHAPTERS`, which `src/lib/routes.ts` derives from `CHAPTERS` — so the
 * routes, the sitemap, the `hreflang` set and this page all come from one list, and
 * a chapter that exists but does not appear is impossible rather than merely
 * unlikely. That was S07's design and this is where it pays.
 *
 * `dynamicParams = false` so an unknown slug is refused by the router; the
 * `notFound()` below is the same statement for anything that reaches the component
 * anyway (a dev-server request, a test).
 *
 * The layout is `src/components/guide/ChapterPage.tsx` — one component for all 34.
 */
export function generateStaticParams(): { locale: string; chapter: string }[] {
  return WWW_LOCALES.flatMap((locale) =>
    GUIDE_CHAPTERS.filter((chapter) => chapter.audience === 'driver').map((chapter) => ({
      locale,
      chapter: chapter.slug,
    })),
  );
}

export const dynamicParams = false;

/**
 * Per-chapter metadata and structured data (S19).
 *
 * `chapterSeo` is the chapter's own title and summary — its `<h1>` and its
 * standfirst — so the tab, the search snippet and the page cannot disagree. The
 * `HowTo` block is built from the same `Step[]` the page renders, which is the
 * payoff S07 named for typing the guide instead of writing 34 MDX files: the
 * structured steps and the visible steps are one array.
 */
export async function generateMetadata({
  params,
}: {
  params: Promise<LocaleParams & { chapter: string }>;
}) {
  const locale = await localeFrom(params);
  const { chapter: slug } = await params;
  const chapter = chapterBySlug('driver', slug);
  if (!chapter) notFound();

  const t = createWwwTranslator(locale);
  const seo = chapterSeo(chapter);

  return metadataFor({
    locale,
    path: `guide/driver/${chapter.slug}`,
    title: t(seo.title),
    description: t(seo.description),
  });
}

export default async function DriverChapterPage({
  params,
}: {
  params: Promise<LocaleParams & { chapter: string }>;
}) {
  const locale = await localeFrom(params);
  const { chapter: slug } = await params;
  const chapter = chapterBySlug('driver', slug);
  if (!chapter) notFound();

  return (
    <>
      <ChapterPage locale={locale} chapter={chapter} />
      <JsonLd nodes={[howTo(locale, chapter), breadcrumbs(chapterCrumbs(locale, chapter))]} />
    </>
  );
}
