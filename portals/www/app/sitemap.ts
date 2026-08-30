import type { MetadataRoute } from 'next';

import { sitemapEntries } from '@/lib/seo';

/**
 * `/sitemap.xml` — **every published URL, generated, never listed.**
 *
 * `sitemapEntries()` maps `allPaths()` (the route table plus the chapter registry)
 * over `WWW_LOCALES`, so the two properties that matter hold by construction rather
 * than by discipline:
 *
 *   - **a chapter added in S23 appears here with no edit to this file**, and
 *   - **a URL that does not render can never appear**, because the same table is
 *     what `generateStaticParams` builds from and what `test/routes.test.ts` holds
 *     against the filesystem in both directions.
 *
 * `test/seo.test.ts` asserts the set equals `allUrls()` exactly — not "contains",
 * *equals* — which is what makes "a new chapter cannot be omitted" true rather than
 * hoped, and what would catch a filter added here for a good reason and forgotten.
 *
 * **No `ta-LK` anywhere** while MCS-34 D2's deferral stands. A sitemap entry for a
 * URL that 404s is the site telling a crawler, in writing, that a document exists.
 *
 * ## No `lastModified`
 *
 * Deliberately absent, and the reason is the one `src/content/legal.ts` gives about
 * dates: the only value available at build time is *now*, which would tell a
 * crawler that all ninety-four documents changed every time the site was rebuilt.
 * That is worse than omitting the field — it trains a crawler to ignore it. When
 * the content pipeline can say when a chapter actually changed, this is where that
 * goes.
 */
export default function sitemap(): MetadataRoute.Sitemap {
  return sitemapEntries().map((entry) => ({
    url: entry.url,
    priority: entry.priority,
    alternates: { languages: entry.alternates },
  }));
}
