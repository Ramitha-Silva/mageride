/**
 * Every URL, title, description and `hreflang` set this site publishes — derived,
 * never written twice.
 *
 * **This is the first MageRide surface that wants to be found.** The other three
 * portals set `robots: { index: false }`, and `portals/web-passenger/app/layout.tsx`
 * gives the reason in its own words: every URL on that host carries somebody's live
 * share token. This surface inverts all of it, so none of the other three is a
 * precedent for anything in this module.
 *
 * ## One table, both directions
 *
 * `hreflang` reciprocity is the rule that is usually got wrong: if `/si/drivers`
 * announces `/en/drivers`, then `/en/drivers` must announce `/si/drivers`, or a
 * search engine discards the annotation on both. It is got wrong because it is
 * usually written per page, and a page only knows about itself.
 *
 * Here every page's set is {@link alternatesFor}, computed from
 * {@link WWW_LOCALES} and the path — so reciprocity is not a property anybody
 * maintains, it is arithmetic. `test/seo.test.ts` asserts it anyway, over every
 * published URL, because "it is generated so it must be right" is how a generator
 * bug survives.
 *
 * ## Only locales that render
 *
 * MCS-34 D2 defers Tamil, so **`ta-LK` appears nowhere** — not in an alternates
 * map, not in the sitemap, not as `x-default`. Advertising a locale that 404s is
 * worse than advertising none: it is the site telling a crawler in writing that a
 * document exists. `x-default` is Sinhala, the platform default (D1′ §283), and
 * `WWW_LOCALES` is the only thing that decides any of it — re-enabling Tamil is
 * still the one constant in `src/i18n/index.ts`.
 *
 * ## `metadataBase` is a constant, and it has to be
 *
 * `https://www.mageride.lk` is written here rather than read from configuration
 * because **this surface reads no configuration at all** — neither the environment
 * nor a build-time public variable, enforced by `test/fences.test.ts` and
 * `scripts/check-bundle.mjs`. There is exactly one canonical origin, the apex 301s
 * to it (S21 · A43), and a canonical URL that varied per deployment would be a
 * canonical URL that could be wrong in production.
 *
 * (Both fences are raw text sweeps and cannot tell a call from a sentence about
 * one, which is deliberate — CLAUDE.md's rule is *describe the rule, do not spell
 * it*, and the paragraph above is deliberately written the long way round for that
 * reason. S19 tried the other repair, weakening the sweep to skip comments, and it
 * is the wrong trade: it buys a comment style with a guarantee.)
 */

import type { Metadata } from 'next';

import { chaptersFor } from '@/content/index';
import { legalDocument } from '@/content/legal';
import { PAGES } from '@/content/pages';
import type { Chapter } from '@/content/types';
import {
  createWwwTranslator,
  DEFAULT_LOCALE,
  HREFLANG,
  WWW_LOCALES,
  type Locale,
  type WwwMessageKey,
} from '@/i18n';
import {
  allPaths,
  GUIDE_AUDIENCES,
  href,
  LEGAL_DOCS,
  ROUTE_BY_PATH,
  routeFor,
  type RoutePath,
} from './routes';

/** The canonical host. The apex 301s to it (S21 · A43). */
export const SITE_ORIGIN = 'https://www.mageride.lk';

/**
 * The locale `x-default` points at.
 *
 * Sinhala, because D1′ §283 makes the platform Sinhala-first and this surface does
 * not get to be the exception for being a marketing page. `x-default` is what a
 * crawler shows a reader whose language matches none of the alternates, and for a
 * Sri Lankan transport platform that reader is better served by Sinhala than by the
 * English cut.
 */
export const X_DEFAULT_LOCALE: Locale = DEFAULT_LOCALE;

/** `https://www.mageride.lk/si/drivers`. Absolute, because a canonical must be. */
export function absoluteUrl(locale: Locale, path: string): string {
  return `${SITE_ORIGIN}${href(locale, path)}`;
}

/**
 * The reciprocal `hreflang` set for one path, plus `x-default`.
 *
 * Keyed by BCP-47 tag (`si-LK`, `en-LK`) rather than by the bare segment: a search
 * engine distinguishing `ta-LK` from `ta-IN` is the whole point of the annotation
 * (A32), and the path stays the two-letter code so `/si/drivers` is short enough to
 * say out loud. `HREFLANG` in `src/i18n/index.ts` is the map, and it is total over
 * `Locale` so re-enabling Tamil does not also have to remember to put its tag back.
 */
export function alternatesFor(path: string): Record<string, string> {
  const languages: Record<string, string> = {};
  for (const locale of WWW_LOCALES) {
    languages[HREFLANG[locale]] = absoluteUrl(locale, path);
  }
  languages['x-default'] = absoluteUrl(X_DEFAULT_LOCALE, path);
  return languages;
}

/**
 * A page's `<title>` and description, as message keys.
 *
 * Resolved from the same registries the page renders, so the tab, the search
 * result and the heading cannot say three different things about one document. The
 * order below is the order of specificity and every branch has a real source:
 *
 *   - **the locale home** has no `PAGES` entry — it is bands, not sections — so it
 *     takes the brand and the tagline;
 *   - **anything in `PAGES`** takes that page's own `title` and `intro`, which S07
 *     wrote as the standfirst and which is exactly what a search snippet wants;
 *   - **a legal document** takes the route's nav label (its `<h1>`, so the two
 *     cannot drift) and `src/content/legal.ts`'s standfirst;
 *   - **a guide chapter** is {@link chapterSeo}, because it is reached with the
 *     chapter in hand rather than by path.
 */
export interface PageSeo {
  readonly title: WwwMessageKey;
  readonly description: WwwMessageKey;
}

export function seoFor(path: RoutePath): PageSeo {
  if (path === '') {
    return { title: 'www.brand.name', description: 'www.brand.tagline' };
  }

  const legal = LEGAL_DOCS.find((doc) => path === `legal/${doc}`);
  if (legal) {
    return {
      title: ROUTE_BY_PATH[path].labelKey,
      description: legalDocument(legal).intro,
    };
  }

  const page = PAGES[path];
  if (page) return { title: page.title, description: page.intro };

  // `guide` is the only remaining route and it is in `PAGES`; this is the branch
  // that catches a fourteenth route added without copy, which would otherwise ship
  // a page with a nav label for a description.
  return { title: ROUTE_BY_PATH[path].labelKey, description: 'www.brand.tagline' };
}

/** A chapter's title and summary — its `<h1>` and its standfirst, again. */
export function chapterSeo(chapter: Chapter): PageSeo {
  return { title: chapter.title, description: chapter.summary };
}

/**
 * The `Metadata` for one page.
 *
 * Everything a search engine and a link preview need, from one call: the title, the
 * description, the canonical, the reciprocal alternates, Open Graph and the Twitter
 * card. `openGraph.images` is deliberately **absent** — Next fills it from the
 * `opengraph-image` route in the segment, and naming a URL here would override the
 * generated card with a guess.
 *
 * `openGraph.locale` uses an underscore (`si_LK`) because that is the Open Graph
 * spec's spelling, where `hreflang` uses a hyphen. The two are the same information
 * in two formats and both come from `HREFLANG`.
 */
export function metadataFor({
  locale,
  path,
  title,
  description,
}: {
  readonly locale: Locale;
  /** Locale-relative, no leading slash — the same spelling `routes.ts` uses. */
  readonly path: string;
  readonly title: string;
  readonly description: string;
}): Metadata {
  const url = absoluteUrl(locale, path);

  return {
    title,
    description,
    alternates: {
      canonical: url,
      languages: alternatesFor(path),
    },
    openGraph: {
      type: 'website',
      siteName: 'MageRide',
      title,
      description,
      url,
      locale: HREFLANG[locale].replace('-', '_'),
      alternateLocale: WWW_LOCALES.filter((other) => other !== locale).map((other) =>
        HREFLANG[other].replace('-', '_'),
      ),
    },
    twitter: {
      card: 'summary_large_image',
      title,
      description,
    },
  };
}

/** `metadataFor`, resolving the strings from the registries. The common case. */
export function metadataForRoute(locale: Locale, path: RoutePath): Metadata {
  const t = createWwwTranslator(locale);
  const seo = seoFor(path);
  return metadataFor({ locale, path, title: t(seo.title), description: t(seo.description) });
}

/**
 * Every published path, in every rendered locale, with its alternates — what
 * `app/sitemap.ts` maps over.
 *
 * Reads `allPaths()` rather than restating the route table, which is what makes
 * *"a chapter added in S23 cannot be left out of the sitemap"* true rather than
 * hoped. `test/seo.test.ts` asserts the sitemap covers `allUrls()` exactly, in both
 * directions.
 */
export interface SitemapEntry {
  readonly url: string;
  readonly alternates: Record<string, string>;
  /**
   * A crawl hint, not a promise. The home page and the role pages change when the
   * marketing copy does; a guide chapter changes when the product does; a legal
   * document changes when counsel says so. Expressed as three values rather than
   * per-route guesses, because a sitemap that claims everything is equally
   * important has said nothing.
   */
  readonly priority: number;
}

export function sitemapEntries(): readonly SitemapEntry[] {
  const chapterPaths = new Set(
    GUIDE_AUDIENCES.flatMap((audience) =>
      chaptersFor(audience).map((chapter) => `guide/${audience}/${chapter.slug}`),
    ),
  );

  /*
   * `allPaths()` exactly — every route in the table plus every guide chapter, and
   * nothing else. The motion workbench (`%5Fmotion-demo`) is not in the table,
   * which `test/routes.test.ts` enforces in both directions, so it cannot leak in
   * here either.
   */
  return WWW_LOCALES.flatMap((locale) =>
    allPaths().map((path) => ({
      url: absoluteUrl(locale, path),
      alternates: alternatesFor(path),
      priority: priorityFor(path, chapterPaths),
    })),
  );
}

function priorityFor(path: string, chapterPaths: ReadonlySet<string>): number {
  if (path === '') return 1;
  if (path.startsWith('legal/')) return 0.3;
  if (chapterPaths.has(path)) return 0.6;
  return 0.8;
}

/**
 * A guide chapter's breadcrumb trail — Home → How to use MageRide → the chapter.
 *
 * Returned as data rather than as JSON-LD so that `src/lib/json-ld.ts` builds the
 * `BreadcrumbList` and a visible breadcrumb, if one is ever added, reads the same
 * array. Structured data that describes something the page does not have is the
 * fence S19 states; the way to keep it true is for both to come from here.
 */
export interface Crumb {
  readonly name: string;
  readonly url: string;
}

export function chapterCrumbs(locale: Locale, chapter: Chapter): readonly Crumb[] {
  const t = createWwwTranslator(locale);

  return [
    { name: t('www.nav.home'), url: absoluteUrl(locale, '') },
    { name: t('www.nav.guide'), url: absoluteUrl(locale, 'guide') },
    {
      name: t(chapter.title),
      url: absoluteUrl(locale, `guide/${chapter.audience}/${chapter.slug}`),
    },
  ];
}

export function legalCrumbs(locale: Locale, doc: string): readonly Crumb[] {
  const t = createWwwTranslator(locale);
  const route = routeFor(`legal/${doc}`);

  return [
    { name: t('www.nav.home'), url: absoluteUrl(locale, '') },
    ...(route
      ? [{ name: t(route.labelKey), url: absoluteUrl(locale, route.path) }]
      : []),
  ];
}

