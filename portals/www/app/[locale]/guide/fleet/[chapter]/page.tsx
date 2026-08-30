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
 * A fleet guide chapter — `/{locale}/guide/fleet/{chapter}` (S23).
 *
 * **This file is the passenger and driver ones with one word changed, and that is
 * the result S23 asked for.** Its brief: *"reuses S17's chapter component unchanged.
 * If it needs a change to accept a third audience, that is a sign S17's component was
 * over-fitted — fix it there rather than forking."* `ChapterPage` needed no change;
 * neither did `ChapterBody`, `chapterLabels`, `chapterSeo`, `chapterCrumbs`, `howTo`
 * or `breadcrumbs`. Every one of them takes a `Chapter` and reads `chapter.audience`
 * off it rather than branching on which of two guides it is in — so the third guide
 * cost a route segment and nothing else.
 *
 * The one thing that could not be shared is this file's existence, because the
 * audience is a *literal path segment* in the App Router and `chapterBySlug` needs to
 * be told which guide `install-and-first-run` belongs to. A `[audience]` dynamic
 * segment would collapse the three, and would also accept `/guide/anything/…` into
 * `generateStaticParams`' filter rather than refusing it at the route.
 *
 * `dynamicParams = false` so an unknown slug is refused by the router; the
 * `notFound()` below is the same statement for anything that reaches the component
 * anyway (a dev-server request, a test).
 */
export function generateStaticParams(): { locale: string; chapter: string }[] {
  return WWW_LOCALES.flatMap((locale) =>
    GUIDE_CHAPTERS.filter((chapter) => chapter.audience === 'fleet').map((chapter) => ({
      locale,
      chapter: chapter.slug,
    })),
  );
}

export const dynamicParams = false;

/**
 * Per-chapter metadata and structured data (S19's shape, unchanged).
 *
 * The `HowTo` block is built from the same `Step[]` the page renders — which on this
 * guide describes a *web portal* rather than an app, and is no less a how-to for it:
 * every one of the six chapters is a procedure with an outcome a reader can check.
 */
export async function generateMetadata({
  params,
}: {
  params: Promise<LocaleParams & { chapter: string }>;
}) {
  const locale = await localeFrom(params);
  const { chapter: slug } = await params;
  const chapter = chapterBySlug('fleet', slug);
  if (!chapter) notFound();

  const t = createWwwTranslator(locale);
  const seo = chapterSeo(chapter);

  return metadataFor({
    locale,
    path: `guide/fleet/${chapter.slug}`,
    title: t(seo.title),
    description: t(seo.description),
  });
}

export default async function FleetChapterPage({
  params,
}: {
  params: Promise<LocaleParams & { chapter: string }>;
}) {
  const locale = await localeFrom(params);
  const { chapter: slug } = await params;
  const chapter = chapterBySlug('fleet', slug);
  if (!chapter) notFound();

  return (
    <>
      <ChapterPage locale={locale} chapter={chapter} />
      <JsonLd nodes={[howTo(locale, chapter), breadcrumbs(chapterCrumbs(locale, chapter))]} />
    </>
  );
}
