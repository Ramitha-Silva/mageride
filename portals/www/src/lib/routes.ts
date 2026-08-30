/**
 * Every route this site publishes, in one module.
 *
 * `app/sitemap.ts`, the header nav, the footer, the `hreflang` block and
 * `test/routes.test.ts` all read this table, which inverts the usual failure: a
 * page that exists and is not listed here is a **test failure** rather than a page
 * nobody links to and no sitemap mentions. On a surface whose entire purpose is to
 * be found by a search engine, an unreachable page is the expensive kind of bug —
 * it looks like nothing at all.
 *
 * Paths are locale-relative and carry no leading slash; `href()` composes the
 * segment. `''` is the locale home — `/si`, `/ta`, `/en` — and is the only empty
 * one.
 */

import { chaptersFor } from '@/content/index.ts';
// `@/i18n/locales` and not `@/i18n`: this module is in the client graph — the nav
// components import `localeRelativePath` — and the translator drags the resource
// tables with it (MCS-36 D3). The published set and its tags live in a table-free
// module for exactly this reason. `WwwMessageKey` is a type and is erased.
import { WWW_LOCALES, type Locale } from '@/i18n/locales';
import type { WwwMessageKey } from '@/i18n/messages/en';

/**
 * Which part of the chrome a route belongs to. The nav and the footer render
 * different groups, so the grouping is data rather than two hand-kept lists that
 * drift.
 */
export type RouteGroup =
  /** In the header nav. */
  | 'primary'
  /** In the footer's support column. */
  | 'support'
  /** In the footer's legal row. */
  | 'legal';

interface RouteDefinition {
  /** Locale-relative, no leading slash. `''` is the locale home. */
  readonly path: string;
  /** The nav label *and* the page heading — one key, so the two cannot disagree. */
  readonly labelKey: WwwMessageKey;
  readonly group: RouteGroup;
}

/**
 * The thirteen top-level routes (A5). Guide **chapter** slugs are not among them —
 * they arrive through {@link GUIDE_CHAPTERS}, derived from the content registry,
 * because the chapter list is a property of the corpus (S08–S11) and not of the
 * routing table.
 *
 * Order is display order: the nav and the footer render this array as-is.
 */
export const ROUTES = [
  { path: '', labelKey: 'www.nav.home', group: 'primary' },
  { path: 'vision', labelKey: 'www.nav.vision', group: 'primary' },
  { path: 'passengers', labelKey: 'www.nav.passengers', group: 'primary' },
  { path: 'drivers', labelKey: 'www.nav.drivers', group: 'primary' },
  { path: 'fleets', labelKey: 'www.nav.fleets', group: 'primary' },
  { path: 'guide', labelKey: 'www.nav.guide', group: 'primary' },
  { path: 'screens', labelKey: 'www.nav.screens', group: 'primary' },
  { path: 'faq', labelKey: 'www.nav.faq', group: 'support' },
  { path: 'download', labelKey: 'www.nav.download', group: 'support' },
  { path: 'contact', labelKey: 'www.nav.contact', group: 'support' },
  { path: 'legal/terms', labelKey: 'www.nav.legal.terms', group: 'legal' },
  { path: 'legal/privacy', labelKey: 'www.nav.legal.privacy', group: 'legal' },
  { path: 'legal/pdpa', labelKey: 'www.nav.legal.pdpa', group: 'legal' },
] as const satisfies readonly RouteDefinition[];

export type Route = (typeof ROUTES)[number];

/**
 * The union of every published path. A literal type rather than `string`, so a
 * component that names a route it invented does not compile.
 */
export type RoutePath = Route['path'];

/**
 * Every route by path.
 *
 * A **total** map over {@link RoutePath}, so a lookup by a path the type system
 * has accepted cannot come back `undefined` and no caller needs a branch for a
 * case that cannot happen. {@link routeFor} is the partial version, for the
 * callers that start from a `string` — a URL segment, a test.
 */
export const ROUTE_BY_PATH = Object.fromEntries(
  ROUTES.map((route) => [route.path, route]),
) as { readonly [P in RoutePath]: Extract<Route, { path: P }> };

/** `'legal/terms'` → `'terms'`. Distributes, so the result is the union. */
type LegalSlug<T> = T extends `legal/${infer Doc}` ? Doc : never;

/**
 * The three documents `app/[locale]/legal/[doc]/page.tsx` renders — derived from
 * the table above rather than restated beside it, in both the type and the value.
 * A fourth legal document is one row in {@link ROUTES}, and the segment's
 * `generateStaticParams` picks it up.
 *
 * MCS-34 D5: the bodies are counsel's, and until they arrive these routes render
 * the scaffold notice. **No session in C134 authors legal text.**
 */
export type LegalDoc = LegalSlug<RoutePath>;

export const LEGAL_DOCS: readonly LegalDoc[] = routesInGroup('legal').map(
  (route) => route.path.slice('legal/'.length) as LegalDoc,
);

/**
 * The three guides, and the segment each lives under.
 *
 * **`'fleet'` was added by S23** — MCS-34 **D7** answered "yes, in the second delivery
 * phase", so the audience that `Chapter['audience']` had always admitted became one
 * this site publishes. Adding it here is the *entire* routing change: `GUIDE_CHAPTERS`
 * maps over this array, `allPaths()` reads that, `app/sitemap.ts` reads that, and the
 * `hreflang` sets follow. Six chapter routes, a sitemap entry each and a reciprocal
 * alternate each appeared without an edit to any of those files.
 *
 * Order is the order `/guide` renders the three sections in, and it is the order a
 * reader is likeliest to want: two end-user apps, then the portal.
 */
export const GUIDE_AUDIENCES = ['passenger', 'driver', 'fleet'] as const;

export type GuideAudience = (typeof GUIDE_AUDIENCES)[number];

export interface GuideChapter {
  readonly audience: GuideAudience;
  /** The URL slug — `guide/passenger/<slug>`. */
  readonly slug: string;
  readonly titleKey: WwwMessageKey;
}

/**
 * The 40 chapter routes — **derived from the content registry, never hand-listed**
 * (S17, extended by S23).
 *
 * S17: *"Generated from the registry, not hand-listed — 34 hand-typed slugs will
 * drift."* They would, and the drift would be silent in the expensive direction: a
 * mistyped slug here compiles, renders a 404 at a URL the sitemap advertises, and
 * looks like a missing page rather than a typo. Reading `CHAPTERS` means the route
 * table cannot disagree with the corpus about which chapters exist, in either
 * direction — and the six chapters S23 added became routes, sitemap entries and
 * `hreflang` sets with no edit to this file.
 *
 * `chaptersFor` sorts by `Chapter.order`, so the reading order S08–S11 set is the
 * order the guide index renders and the order `ChapterPager` walks.
 *
 * **S23 proved the derivation.** The fleet audience used to be deliberately absent
 * from {@link GUIDE_AUDIENCES} while `Chapter['audience']` already admitted it, so a
 * fleet chapter in the registry published nothing rather than 404ing at a URL nothing
 * linked to. Publishing the fleet guide was one array member: **40 chapter routes
 * instead of 34**, with no slug typed into this file in either pass.
 */
export const GUIDE_CHAPTERS: readonly GuideChapter[] = GUIDE_AUDIENCES.flatMap((audience) =>
  chaptersFor(audience).map((chapter) => ({
    audience,
    slug: chapter.slug,
    titleKey: chapter.title,
  })),
);

/** The URL for a route in a locale. `/si`, `/en/drivers`, `/ta/legal/pdpa`. */
export function href(locale: Locale, path: string): string {
  return path === '' ? `/${locale}` : `/${locale}/${path}`;
}

/**
 * Where a "read the guide" link should point for an audience — **the deepest link
 * that actually exists** (S16).
 *
 * S16 asks `/drivers` to link "specifically into the onboarding chapters", and in
 * the same breath: *"Never render a link to a chapter that does not exist —
 * `test/routes.test.ts` will catch it, but it should not get that far."* Those two
 * are only compatible if the answer is computed rather than written down, because
 * {@link GUIDE_CHAPTERS} was **empty until S17**: every `guide/driver/<slug>` was a
 * 404 and every one of them became a real URL the moment S17 filled the table.
 *
 * So this returns the first chapter for the audience if one is registered, and the
 * guide index if none is. **Neither S17 nor S23 changed a line here** — filling
 * `GUIDE_CHAPTERS` turned the role pages' index links into deep links on its own,
 * which is the property that stopped this being a hardcoded `'guide'` somebody had to
 * remember to revisit. S23 is the second proof: `/fleets` now deep-links into
 * `guide/fleet/registering-your-organisation` because that chapter exists, and the
 * only edit was `roles.ts` naming the audience instead of `'contact'`.
 *
 * The *first* chapter and not an arbitrary one because the two app guides open with
 * `install-and-first-run`: a driver arriving from an advertisement wants to know what
 * documents they need before they download anything, and that is chapter 1. The fleet
 * guide opens on registering the organisation for the same reason — it is the gate
 * everything else waits behind (US-13.A7).
 */
export function guideEntryPath(audience: GuideAudience): string {
  const first = GUIDE_CHAPTERS.find((chapter) => chapter.audience === audience);
  return first ? `guide/${audience}/${first.slug}` : 'guide';
}

/**
 * `/si/legal/pdpa` → `legal/pdpa`. The inverse of {@link href}.
 *
 * The locale switcher needs "this same document, in the other language", and the
 * only thing that knows which document is showing is the URL. Stripping the first
 * segment is the whole operation — locales are always exactly one segment and
 * always first, which is what makes `hreflang` reciprocity mechanical rather than
 * a per-page mapping (A32).
 *
 * Returns `''` for a locale home, which is what `href` takes for the same page.
 * A pathname with no recognised locale prefix comes back unchanged with its
 * leading slash removed — it cannot happen through the router, which refuses an
 * unknown locale before any of this runs, and returning something sane beats
 * throwing inside a header that renders on every page.
 */
export function localeRelativePath(pathname: string): string {
  const trimmed = pathname.replace(/^\/+/, '').replace(/\/+$/, '');
  if (trimmed === '') return '';

  const [first, ...rest] = trimmed.split('/');
  return WWW_LOCALES.includes(first as Locale) ? rest.join('/') : trimmed;
}

/** The routes in one group, in display order. */
export function routesInGroup(group: RouteGroup): readonly Route[] {
  return ROUTES.filter((route) => route.group === group);
}

/** A route by path, or `undefined` if this site does not publish one. */
export function routeFor(path: string): Route | undefined {
  return ROUTES.find((route) => route.path === path);
}

/**
 * Every locale-relative path the site publishes: the table, plus whatever guide
 * chapters exist. `app/sitemap.ts` (S19) multiplies this by {@link WWW_LOCALES};
 * the multiplication is deliberately not done here, because the sitemap also needs
 * a per-URL `alternates` map and would have to take the product apart again.
 */
export function allPaths(): readonly string[] {
  return [
    ...ROUTES.map((route) => route.path),
    ...GUIDE_CHAPTERS.map((chapter) => `guide/${chapter.audience}/${chapter.slug}`),
  ];
}

/**
 * Every published URL, in every **published** locale.
 *
 * `WWW_LOCALES` and not the platform's `LOCALES`: MCS-34 D2 defers Tamil on this
 * surface, so no `/ta/…` URL is published, sitemapped or named in an `hreflang`
 * set. A sitemap listing a URL that 404s is worse than one that omits it — it is
 * the site telling a crawler, in writing, that a document exists.
 */
export function allUrls(): readonly string[] {
  return WWW_LOCALES.flatMap((locale) => allPaths().map((path) => href(locale, path)));
}
