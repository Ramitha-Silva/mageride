import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { LOCALES } from '@mageride/i18n';
import { FAQ } from '@/content/faq';
import { chaptersFor } from '@/content/index';
import { LEGAL_DOCUMENTS } from '@/content/legal';
import { HREFLANG, WWW_LOCALES, type Locale } from '@/i18n';
import {
  breadcrumbs,
  faqPage,
  howTo,
  legalPage,
  organization,
  softwareApplications,
  webSite,
} from '@/lib/json-ld';
import { allPaths, GUIDE_AUDIENCES, GUIDE_CHAPTERS, LEGAL_DOCS } from '@/lib/routes';
import {
  absoluteUrl,
  alternatesFor,
  chapterCrumbs,
  legalCrumbs,
  SITE_ORIGIN,
  sitemapEntries,
  X_DEFAULT_LOCALE,
} from '@/lib/seo';

/**
 * **This is the first MageRide surface that wants to be found**, and that inverts
 * the assumption every other portal is built on. `web-passenger` sets
 * `robots: { index: false }` because every URL on that host carries somebody's live
 * share token; here the whole point is that a crawler reads all of it. So none of
 * the other three portals is a precedent, and the things that go wrong are the
 * things nobody looks at: a `hreflang` set that points one way, a sitemap missing
 * the chapter added last week, a `potentialAction` describing a search box that
 * does not exist.
 *
 * Every one of those failures is **invisible in a browser**. The page renders, the
 * links work, and the damage is done in an index nobody on the team reads. That is
 * the whole argument for asserting them here rather than reviewing them: S19 names
 * reciprocity and 100% route coverage as test obligations precisely because they
 * are not observable from the page.
 */

const appDir = resolve(import.meta.dirname, '../app/[locale]');

describe('hreflang', () => {
  /**
   * **Reciprocity, generated rather than hand-written.**
   *
   * Google's rule is that the annotation must be confirmed from the other side: if
   * `/si/drivers` lists `/en/drivers` and `/en/drivers` does not list back, the set
   * is discarded — silently, and for every page in it. Both sides come out of
   * `alternatesFor(path)`, which takes a *path* and not a locale, so the two
   * renderings are the same object by construction; this test is what stops a
   * later "optimisation" from making them per-locale again.
   */
  it('is reciprocal — every locale lists every other locale for the same path', () => {
    for (const path of allPaths()) {
      const alternates = alternatesFor(path);

      for (const locale of WWW_LOCALES) {
        const url = absoluteUrl(locale, path);
        expect(
          Object.values(alternates),
          `${url} must appear in its own alternates set`,
        ).toContain(url);

        for (const other of WWW_LOCALES) {
          expect(
            alternates[HREFLANG[other]],
            `${url} must list ${other} for the same path`,
          ).toBe(absoluteUrl(other, path));
        }
      }
    }
  });

  /**
   * **Only rendered locales**, and Tamil is the live case rather than a hypothetical.
   *
   * MCS-34 D2 defers Tamil: `WWW_LOCALES` is `['si', 'en']`, `/ta/anything` is not
   * built and answers 404. Advertising `ta-LK` would send a crawler — and every
   * Tamil reader it then serves — to a page that does not exist, which is strictly
   * worse than advertising nothing. The assertion is written against `LOCALES`
   * (all three) minus `WWW_LOCALES` (the published ones) so that **re-enabling
   * Tamil makes this test pass without an edit**, which is what CLAUDE.md promises
   * when it says re-enabling is one line.
   */
  it('never advertises a locale that does not render', () => {
    const unpublished = LOCALES.filter((locale) => !WWW_LOCALES.includes(locale));

    for (const path of allPaths()) {
      const alternates = alternatesFor(path);

      for (const locale of unpublished) {
        expect(
          alternates,
          `${path}: ${HREFLANG[locale]} is not a published locale`,
        ).not.toHaveProperty(HREFLANG[locale]);
      }

      for (const url of Object.values(alternates)) {
        for (const locale of unpublished) {
          expect(url, `${path}: no alternate may point at /${locale}`).not.toContain(
            `${SITE_ORIGIN}/${locale}/`,
          );
        }
      }
    }
  });

  /**
   * `x-default` is the platform default, not English.
   *
   * S19 says it points at Sinhala while Tamil is deferred, and the reason is that
   * `x-default` answers "what should a reader we cannot place get?" — on a Sri
   * Lankan transport platform that is Sinhala. It follows `X_DEFAULT_LOCALE`, which
   * follows `DEFAULT_LOCALE`, so it moves with the platform rather than with this
   * file.
   */
  it('points x-default at the platform default locale', () => {
    expect(WWW_LOCALES).toContain(X_DEFAULT_LOCALE);

    for (const path of allPaths()) {
      expect(alternatesFor(path)['x-default']).toBe(absoluteUrl(X_DEFAULT_LOCALE, path));
    }
  });
});

describe('sitemap', () => {
  /**
   * **100% route coverage, which is what makes "a new chapter cannot be omitted"
   * true rather than hoped.**
   *
   * `sitemapEntries()` reads `allPaths()`, and `test/routes.test.ts` already holds
   * `allPaths()` to the filesystem in both directions. Chaining the two is the
   * whole guarantee: a page can only exist if it is in the route table, and a route
   * table entry can only exist if it is in the sitemap. A chapter added in a later
   * session is therefore crawlable without anyone remembering this file.
   */
  it('lists every route in every rendered locale, and nothing else', () => {
    const expected = new Set(
      WWW_LOCALES.flatMap((locale) => allPaths().map((path) => absoluteUrl(locale, path))),
    );
    const actual = new Set(sitemapEntries().map((entry) => entry.url));

    expect(actual).toEqual(expected);
    expect(sitemapEntries()).toHaveLength(expected.size);
  });

  it('carries the same reciprocal alternates on every entry', () => {
    for (const entry of sitemapEntries()) {
      const locale = entry.url.slice(`${SITE_ORIGIN}/`.length).split('/')[0] as Locale;
      const path = entry.url.slice(`${SITE_ORIGIN}/${locale}`.length).replace(/^\//, '');

      expect(entry.alternates).toEqual(alternatesFor(path));
      expect(Object.values(entry.alternates)).toContain(entry.url);
    }
  });

  /**
   * A page on disk that is not in the table must not be sitemapped.
   *
   * Written against the *directory listing* rather than against a known offender,
   * so it survives the offender changing. When S19 wrote it there was one —
   * `app/[locale]/%5Fmotion-demo/`, S04's workbench, deliberately outside the table
   * — and S20 deleted it, so today this asserts over an empty set. That is the
   * right shape to leave behind: the risk was never that the workbench was linked,
   * it was that a future sitemap builder would walk the filesystem instead of the
   * table, and that risk outlives any particular page.
   */
  it('omits every page that is not in the route table', async () => {
    const onDisk = (await readdir(appDir, { withFileTypes: true }))
      .filter((entry) => entry.isDirectory())
      .map((entry) => decodeURIComponent(entry.name));

    const untabled = onDisk.filter((name) => !allPaths().some((path) => path.startsWith(name)));
    const urls = sitemapEntries().map((entry) => entry.url);

    for (const name of untabled) {
      for (const url of urls) {
        expect(url, `${name} is not in the route table and must not be in the sitemap`).not.toContain(
          `/${name}`,
        );
      }
    }
  });

  it('is absolute, canonical-host and https throughout', () => {
    for (const entry of sitemapEntries()) {
      expect(entry.url.startsWith(`${SITE_ORIGIN}/`)).toBe(true);
      for (const url of Object.values(entry.alternates)) {
        expect(url.startsWith('https://www.mageride.lk/')).toBe(true);
      }
    }
  });
});

describe('JSON-LD', () => {
  /**
   * **`WebSite` ships without `SearchAction`, and this is the assertion S19 asks
   * for by name.**
   *
   * The block is nearly always copied with a `potentialAction` attached, because
   * every example on the web has one. This site has no search endpoint — there is
   * no `/search`, and adding one would need a request-time dependency MCS-34's
   * fourth negative forbids — so a `SearchAction` would declare a capability that
   * does not exist. Structured data that describes something the page does not have
   * is the fence, and this is the case most likely to walk back in.
   */
  it('declares no SearchAction, because there is no search', () => {
    for (const locale of WWW_LOCALES) {
      const site = webSite(locale);
      expect(site['@type']).toBe('WebSite');
      expect(site).not.toHaveProperty('potentialAction');
      expect(JSON.stringify(site)).not.toContain('SearchAction');
    }
  });

  /**
   * No `installUrl` while the listings are unpublished (MCS-34 D3).
   *
   * Same principle, different claim: a store URL that 404s is a false declaration,
   * and it is one a reader meets rather than a crawler — Google renders app cards
   * from this block. When the listings are real, this test is the reminder that the
   * URL belongs in `json-ld.ts` beside the visible download links, not only here.
   */
  it('claims no store URL for either app', () => {
    for (const locale of WWW_LOCALES) {
      const apps = softwareApplications(locale);
      expect(apps).toHaveLength(2);

      for (const app of apps) {
        expect(app['@type']).toBe('SoftwareApplication');
        expect(app).not.toHaveProperty('installUrl');
        expect(app).toHaveProperty('offers');
        expect(app.name).toBeTruthy();
      }
    }
  });

  /**
   * **`FAQPage` and the rendered accordion are the same entries by construction.**
   *
   * Marking up answers a visitor cannot reach is the abuse the guideline names, and
   * the reason this surface is safe from it is S18's `<details>` decision: every
   * answer is in the DOM whether its item is open or closed. Both sides read `FAQ`,
   * so the test is really asserting that nobody has introduced a second list.
   */
  it('builds FAQPage from the same array the page renders', () => {
    for (const locale of WWW_LOCALES) {
      const page = faqPage(locale);
      expect(page['@type']).toBe('FAQPage');

      const questions = page.mainEntity as readonly Record<string, unknown>[];
      expect(questions).toHaveLength(FAQ.length);

      for (const entry of questions) {
        expect(entry['@type']).toBe('Question');
        expect(entry.name).toBeTruthy();
        const answer = entry.acceptedAnswer as Record<string, unknown>;
        expect(answer['@type']).toBe('Answer');
        expect(String(answer.text ?? '')).not.toHaveLength(0);
      }
    }
  });

  /**
   * `HowTo` on **every** chapter — the payoff for S07's uniform `Step[]` shape.
   *
   * Written as a sweep rather than a spot check because the value is in the "every":
   * 34 chapters map to 34 `HowTo` blocks with no per-chapter code, and the way that
   * stops being true is one chapter growing a shape of its own.
   */
  it('emits a HowTo for every guide chapter, with a step per step', () => {
    for (const locale of WWW_LOCALES) {
      for (const audience of GUIDE_AUDIENCES) {
        for (const chapter of chaptersFor(audience)) {
          const block = howTo(locale, chapter);

          expect(block['@type']).toBe('HowTo');
          expect(block.url).toBe(
            absoluteUrl(locale, `guide/${chapter.audience}/${chapter.slug}`),
          );

          const steps = block.step as readonly Record<string, unknown>[];
          expect(steps, `${chapter.id} must map every step`).toHaveLength(chapter.steps.length);

          steps.forEach((step, index) => {
            expect(step['@type']).toBe('HowToStep');
            expect(step.position).toBe(index + 1);
            expect(String(step.text ?? '')).not.toHaveLength(0);
          });
        }
      }
    }
  });

  it('covers every chapter route with a HowTo', () => {
    const covered = new Set(
      GUIDE_AUDIENCES.flatMap((audience) =>
        chaptersFor(audience).map((chapter) => `guide/${audience}/${chapter.slug}`),
      ),
    );
    for (const chapter of GUIDE_CHAPTERS) {
      expect(covered).toContain(`guide/${chapter.audience}/${chapter.slug}`);
    }
  });

  /**
   * Breadcrumbs on chapters and legal documents, positioned from 1 and absolute.
   *
   * A `BreadcrumbList` whose `item` is a relative path is the common mistake and it
   * is accepted silently by the validators, so it is worth pinning.
   */
  it('builds absolute, 1-based breadcrumb trails', () => {
    for (const locale of WWW_LOCALES) {
      /*
       * `chaptersFor()` and not `GUIDE_CHAPTERS`. Two different types share that
       * name: the content registry's `Chapter` — which `chapterCrumbs` takes, and
       * which has `title` — and `routes.ts`'s `GuideChapter`, which is the routing
       * projection and has `titleKey`. Passing the second produces a trail whose
       * last crumb is unnamed, silently, which is what this caught.
       */
      const trails = [
        ...GUIDE_AUDIENCES.flatMap((audience) =>
          chaptersFor(audience).map((chapter) => chapterCrumbs(locale, chapter)),
        ),
        ...LEGAL_DOCS.map((doc) => legalCrumbs(locale, doc)),
      ];

      for (const trail of trails) {
        const list = breadcrumbs(trail);
        expect(list['@type']).toBe('BreadcrumbList');

        const items = list.itemListElement as readonly Record<string, unknown>[];
        expect(items.length).toBeGreaterThanOrEqual(2);

        items.forEach((item, index) => {
          expect(item.position).toBe(index + 1);
          expect(String(item.item ?? '')).toMatch(/^https:\/\/www\.mageride\.lk\//);
          expect(item.name).toBeTruthy();
        });
      }
    }
  });

  it('gives every legal document a WebPage block', () => {
    for (const locale of WWW_LOCALES) {
      for (const document of LEGAL_DOCUMENTS) {
        const block = legalPage(locale, document);
        expect(block['@type']).toBe('WebPage');
        expect(block.url).toBe(absoluteUrl(locale, `legal/${document.doc}`));
      }
    }
  });

  it('names the organisation on the canonical host', () => {
    for (const locale of WWW_LOCALES) {
      const org = organization(locale);
      expect(org['@type']).toBe('Organization');
      expect(org.url).toBe(SITE_ORIGIN);
    }
  });

  /**
   * Nothing emitted here may be a hand-written literal.
   *
   * Every block above is built from the registries the pages render, and the way
   * that breaks is somebody adding a node with prose typed into it — which would
   * also be untranslated, and so would describe the Sinhala page in English. The
   * cheap proof is that no block differs only by locale-independent text: build the
   * whole set twice and require the two to differ.
   */
  it('renders locale-specific structured data rather than one fixed copy', () => {
    const [first, ...rest] = WWW_LOCALES;
    expect(first, 'the surface publishes at least one locale').toBeDefined();
    if (!first) return;

    for (const other of rest) {
      expect(JSON.stringify(faqPage(first))).not.toBe(JSON.stringify(faqPage(other)));
      expect(JSON.stringify(webSite(first))).not.toBe(JSON.stringify(webSite(other)));
    }
  });
});

describe('cache headers (S21 · A44)', () => {
  /**
   * **Asserted against `next.config.ts` itself, not against a running server**, and the
   * choice is deliberate: this suite runs in a second with no build behind it, and the
   * property worth pinning is the *policy*, not that Next can attach a header.
   *
   * S20 left these pending because S21 is what sets them — cache lifetimes arrived with
   * the ingress that serves the host. They are here rather than at the edge because Next
   * knows which response it is serving and an nginx `location` regex does not; the edge
   * keeps TLS, HSTS and the apex 301.
   */
  const config = async () =>
    readFile(resolve(import.meta.dirname, '../next.config.ts'), 'utf8');

  it('makes the content-hashed and committed assets immutable', async () => {
    const text = await config();

    expect(text).toMatch(/max-age=31536000/);
    expect(text).toMatch(/immutable/);
    // Both immutable classes are named, and both are safe to be: a hashed build path and a
    // committed image that only `npm run screens:refresh` changes.
    expect(text).toMatch(/\/_next\/static\/:path\*/);
    expect(text).toMatch(/\/screens\/:path\*/);
  });

  /**
   * **`stale-while-revalidate` is the "renders with the backend down" fence extended one
   * layer out**, and it is the clause most likely to be dropped by somebody tightening
   * cache times. Without it a cache that cannot reach the origin serves nothing; with it
   * the site survives the *cluster* being down for a day, not just the platform.
   */
  it('lets a shared cache serve the site while the origin is unreachable', async () => {
    const text = await config();

    expect(text).toMatch(/s-maxage=300/);
    expect(text).toMatch(/stale-while-revalidate=86400/);
  });

  /**
   * `s-maxage`, not `max-age`, on HTML — the shared cache holds the copy and the reader's
   * browser revalidates. A page corrected at 09:00 must not sit stale in somebody's tab.
   */
  it('does not give HTML a browser-side lifetime', async () => {
    const text = await config();
    const html = /const HTML_CACHE = '([^']+)'/.exec(text)?.[1] ?? '';

    expect(html, 'HTML_CACHE not found in next.config.ts').toBeTruthy();
    expect(html).toContain('s-maxage=300');
    expect(html).not.toMatch(/(^|[,\s])max-age=/);
  });

  /**
   * **The catch-all must come before the immutable rules**, and this is the assertion that
   * would have caught the bug S21 shipped for one build.
   *
   * Next applies every entry whose `source` matches and the *last* match wins for a given
   * header name. Written the intuitive way round — specific first — the broad `/:path*`
   * rule silently overwrote both immutable classes, and `/_next/static/*.css` came back
   * `s-maxage=300`. Nothing about that is visible in the config or in a type error; it is
   * visible only in a response header. Found with `curl`, kept with this.
   */
  it('orders the catch-all before the rules it would otherwise override', async () => {
    const text = await config();

    const catchAll = text.indexOf("source: '/:path*'");
    const staticRule = text.indexOf("source: '/_next/static/:path*'");
    const screensRule = text.indexOf("source: '/screens/:path*'");

    expect(catchAll, 'no catch-all header rule').toBeGreaterThan(-1);
    expect(staticRule, 'no /_next/static rule').toBeGreaterThan(-1);
    expect(screensRule, 'no /screens rule').toBeGreaterThan(-1);

    expect(catchAll).toBeLessThan(staticRule);
    expect(catchAll).toBeLessThan(screensRule);
  });

  it('sets the two document headers that are this surface’s to set', async () => {
    const text = await config();

    expect(text).toMatch(/X-Content-Type-Options/);
    expect(text).toMatch(/nosniff/);
    expect(text).toMatch(/Referrer-Policy/);
    expect(text).toMatch(/strict-origin-when-cross-origin/);
  });
});

describe('canonical host', () => {
  it('is the www host every absolute URL is built from', () => {
    expect(SITE_ORIGIN).toBe('https://www.mageride.lk');

    for (const locale of WWW_LOCALES) {
      expect(absoluteUrl(locale, '')).toBe(`${SITE_ORIGIN}/${locale}`);
      expect(absoluteUrl(locale, 'drivers')).toBe(`${SITE_ORIGIN}/${locale}/drivers`);
    }
  });

  /**
   * **One source for `robots.txt`.**
   *
   * `app/robots.ts` generates it. A `public/robots.txt` beside it would win — Next
   * serves `public/` ahead of the route — and would do so silently, which is how a
   * stale `Disallow: /` outlives the decision that added it. S19 deletes the static
   * file; this keeps it deleted.
   */
  it('has no static robots.txt competing with app/robots.ts', async () => {
    const publicDir = resolve(import.meta.dirname, '../public');
    const entries = await readdir(publicDir);
    expect(entries).not.toContain('robots.txt');
    expect(entries).not.toContain('sitemap.xml');
  });
});
