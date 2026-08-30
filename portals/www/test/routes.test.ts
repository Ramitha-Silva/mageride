import { readdir, readFile, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { LOCALES } from '@mageride/i18n';
import { WWW_LOCALES } from '@/i18n';
import { chaptersFor } from '@/content/index';
import {
  allPaths,
  allUrls,
  GUIDE_AUDIENCES,
  GUIDE_CHAPTERS,
  href,
  LEGAL_DOCS,
  ROUTES,
  ROUTE_BY_PATH,
  routeFor,
} from '@/lib/routes';

/**
 * `src/lib/routes.ts` is only worth having if it cannot disagree with the
 * filesystem. These tests are that guarantee — they walk `app/[locale]/` and hold
 * the table and the tree to each other in **both** directions:
 *
 *   - a page that exists and is not in the table would be a page the nav never
 *     links, the sitemap never lists and no crawler ever finds. On a surface whose
 *     purpose is to be found, that failure looks like nothing at all, which is why
 *     it needs a test rather than a review;
 *   - a table entry with no page behind it would be a 404 in the sitemap.
 *
 * S17 adds the guide chapter slugs and S18 the remaining page bodies; neither can
 * do so without this file staying green, which is the point.
 */

const appDir = resolve(import.meta.dirname, '../app/[locale]');

/**
 * **There is no exemption any more, and that is S20 finishing what S04 started.**
 *
 * One page used to be allowed to exist without a row in the table:
 * `app/[locale]/%5Fmotion-demo/`, S04's workbench — every motion primitive rendered
 * once so the layer could be checked by eye in both appearances and with reduced
 * motion forced. S20 deletes it, along with its 23 message keys in all three
 * tables, because `test/a11y.test.ts` now asserts by machine what the workbench
 * showed by eye.
 *
 * The empty set is kept rather than the check being simplified away, and the
 * reason is the shape it had: **an exemption by exact name**, never a
 * `startsWith('%5F')` pattern that would have silently covered the next one. If a
 * later session genuinely needs an unpublished page, it goes here with a named
 * reason and the test below still holds it to existing. On a surface whose whole
 * purpose is to be found, a page in no nav, no sitemap and no `hreflang` set is
 * normally the bug this file exists to catch.
 */
const UNPUBLISHED_PAGES = new Set<string>();

/** Every `page.tsx` below `app/[locale]/`, as a locale-relative route pattern. */
async function pagePatterns(dir = appDir, prefix = ''): Promise<string[]> {
  const entries = await readdir(dir, { withFileTypes: true });
  const found: string[] = [];

  for (const entry of entries) {
    if (entry.isDirectory()) {
      const next = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
      found.push(...(await pagePatterns(join(dir, entry.name), next)));
    } else if (entry.name === 'page.tsx') {
      found.push(prefix);
    }
  }

  return found;
}

const DYNAMIC_SEGMENT = /\[[^\]]+\]/;

describe('the route table', () => {
  it('has no duplicate path', () => {
    const paths = ROUTES.map((route) => route.path);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it('has exactly one home, and it is the only empty path', () => {
    expect(ROUTES.filter((route) => route.path === '')).toHaveLength(1);
  });

  it('carries no leading or trailing slash on any path', () => {
    for (const route of ROUTES) {
      expect(route.path.startsWith('/')).toBe(false);
      expect(route.path.endsWith('/')).toBe(false);
    }
  });

  it('is a total map by path', () => {
    for (const route of ROUTES) {
      expect(ROUTE_BY_PATH[route.path]).toBe(route);
      expect(routeFor(route.path)).toBe(route);
    }
    expect(routeFor('not-a-route')).toBeUndefined();
  });

  it('derives the legal document slugs from itself', () => {
    expect([...LEGAL_DOCS]).toEqual(['terms', 'privacy', 'pdpa']);
  });
});

describe('the table and the app tree agree', () => {
  it('has a page behind every route in the table', async () => {
    const patterns = await pagePatterns();
    const staticPages = new Set(patterns.filter((pattern) => !DYNAMIC_SEGMENT.test(pattern)));
    const dynamicPages = patterns.filter((pattern) => DYNAMIC_SEGMENT.test(pattern));

    for (const route of ROUTES) {
      const served =
        staticPages.has(route.path) ||
        dynamicPages.some((pattern) => matchesPattern(route.path, pattern));

      expect(served, `no page renders "${route.path}"`).toBe(true);
    }
  });

  it('lists every static page in the table', async () => {
    const patterns = await pagePatterns();

    for (const pattern of patterns) {
      if (DYNAMIC_SEGMENT.test(pattern) || UNPUBLISHED_PAGES.has(pattern)) continue;
      expect(routeFor(pattern), `app/[locale]/${pattern}/page.tsx is not in ROUTES`).toBeDefined();
    }
  });

  /**
   * The exemption list is checked in the other direction too, so an exemption
   * cannot outlive what it exempts. **This is the test that fired when S20 deleted
   * the workbench** — exactly as S04 designed it to — and it is why the set is by
   * exact name. It now holds an empty set to nothing, and starts working again the
   * moment somebody adds a name.
   */
  it('exempts nothing that is not there', async () => {
    const patterns = new Set(await pagePatterns());

    for (const exempt of UNPUBLISHED_PAGES) {
      expect(patterns.has(exempt), `UNPUBLISHED_PAGES names "${exempt}", which has no page`).toBe(
        true,
      );
    }
  });

  /**
   * A dynamic segment publishes URLs the table cannot see unless its
   * `generateStaticParams` reads the table. Asserted on the import rather than by
   * executing the module: these are server components with `next/*` imports that a
   * jsdom test has no business instantiating, and the property that matters — the
   * slugs come from one place — is visible in the import list.
   */
  it('feeds every dynamic segment from the route table', async () => {
    const patterns = await pagePatterns();

    for (const pattern of patterns) {
      if (!DYNAMIC_SEGMENT.test(pattern)) continue;
      const source = await readFile(join(appDir, pattern, 'page.tsx'), 'utf8');
      expect(source, `app/[locale]/${pattern}/page.tsx invents its own params`).toMatch(
        /from '@\/lib\/routes'/,
      );
      expect(source, `app/[locale]/${pattern}/page.tsx accepts unknown slugs`).toMatch(
        /export const dynamicParams = false/,
      );
    }
  });
});

describe('URL composition', () => {
  it('puts the locale first and omits the trailing slash on a home', () => {
    expect(href('si', '')).toBe('/si');
    expect(href('en', 'drivers')).toBe('/en/drivers');
    expect(href('si', 'legal/pdpa')).toBe('/si/legal/pdpa');
  });

  /**
   * `href` is total over `Locale` and stays that way: it is a string composer, not
   * a publication decision. Composing a Tamil URL is legal; *publishing* one is
   * what `allUrls()` decides, and that is the assertion below.
   */
  it('composes a URL for a locale this surface does not publish', () => {
    expect(href('ta', 'legal/pdpa')).toBe('/ta/legal/pdpa');
    expect(LOCALES).toContain('ta');
  });

  it('publishes every path in every published locale', () => {
    expect(allUrls()).toHaveLength(allPaths().length * WWW_LOCALES.length);
    for (const locale of WWW_LOCALES) {
      expect(allUrls()).toContain(href(locale, 'vision'));
    }
  });

  /**
   * S17 filled `GUIDE_CHAPTERS`. This replaces the assertion that it was empty.
   *
   * The property worth holding is not the count — it is that **the route table and
   * the content registry cannot disagree about which chapters exist**, because the
   * table derives from the registry rather than restating it. So the test compares
   * the two rather than checking 34 against a literal: a chapter added in S23 makes
   * this pass without an edit, and a slug typed by hand into `routes.ts` makes it
   * fail.
   */
  it('derives every chapter route from the content registry', () => {
    const fromRegistry = GUIDE_AUDIENCES.flatMap((audience) =>
      chaptersFor(audience).map((chapter) => `guide/${audience}/${chapter.slug}`),
    );

    expect(GUIDE_CHAPTERS.map((c) => `guide/${c.audience}/${c.slug}`)).toEqual(fromRegistry);
    expect(allPaths()).toEqual([...ROUTES.map((route) => route.path), ...fromRegistry]);
  });

  /**
   * **The counts are literals here on purpose, and they are the one place on this
   * surface where that is right.** Everything else about the guide is derived — the
   * routes from the registry, the sitemap from the routes — which is exactly why a
   * chapter that silently stopped being registered would propagate cleanly all the
   * way to a smaller sitemap and break nothing. These three numbers are the outside
   * observer: 16 + 18 + 6, as S08–S11 and S23 delivered them.
   *
   * S23 added the third audience, and the shape of the edit is the point: one entry
   * appended to `GUIDE_AUDIENCES`, one line appended here. No slug was typed twice.
   */
  it('publishes 16 passenger, 18 driver and 6 fleet chapters, in reading order', () => {
    const passenger = GUIDE_CHAPTERS.filter((c) => c.audience === 'passenger');
    const driver = GUIDE_CHAPTERS.filter((c) => c.audience === 'driver');
    const fleet = GUIDE_CHAPTERS.filter((c) => c.audience === 'fleet');

    expect(passenger).toHaveLength(16);
    expect(driver).toHaveLength(18);
    expect(fleet).toHaveLength(6);
    expect(GUIDE_CHAPTERS).toHaveLength(40);

    // `chaptersFor` sorts by `order`, so the table's order is the reading order —
    // which is what `ChapterPager` walks and what the guide index renders.
    expect(passenger.map((c) => c.slug)).toEqual(
      chaptersFor('passenger').map((chapter) => chapter.slug),
    );
    expect(driver.map((c) => c.slug)).toEqual(chaptersFor('driver').map((chapter) => chapter.slug));
    expect(fleet.map((c) => c.slug)).toEqual(chaptersFor('fleet').map((chapter) => chapter.slug));
  });

  /**
   * Every audience in the table publishes a route segment, and every segment has an
   * audience. S23 is why this exists: `Chapter['audience']` admitted `'fleet'` for
   * five sessions while `GUIDE_AUDIENCES` did not, so six written chapters published
   * nothing — a deliberate state at the time, and an indistinguishable one from the
   * bug where somebody forgets the array. This is what tells the two apart now.
   */
  it('gives every guide audience a route segment under app/[locale]/guide', async () => {
    const guideRoot = join(appDir, 'guide');

    for (const audience of GUIDE_AUDIENCES) {
      const page = join(guideRoot, audience, '[chapter]', 'page.tsx');
      await expect(
        stat(page),
        `${audience} chapters are registered but app/[locale]/guide/${audience}/[chapter]/page.tsx does not exist`,
      ).resolves.toBeTruthy();
    }
  });

  /**
   * The two guides genuinely share slugs — `install-and-first-run` is chapter 1 of
   * each — so uniqueness is per audience, and a URL is the pair. A test on slugs
   * alone would either fail on a legitimate collision or miss a real duplicate.
   */
  it('gives every chapter a unique URL', () => {
    const urls = GUIDE_CHAPTERS.map((c) => `${c.audience}/${c.slug}`);
    expect(new Set(urls).size).toBe(urls.length);
    expect(new Set(GUIDE_CHAPTERS.map((c) => c.slug)).size).toBeLessThan(urls.length);
  });
});

/** Does a concrete path match a Next route pattern with `[segments]` in it? */
function matchesPattern(path: string, pattern: string): boolean {
  const pathParts = path.split('/');
  const patternParts = pattern.split('/');
  if (pathParts.length !== patternParts.length) return false;

  return patternParts.every((part, index) =>
    DYNAMIC_SEGMENT.test(part) ? true : part === pathParts[index],
  );
}
