import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { createTranslator, isLocale, LOCALES, negotiateLocale, type MessageKey } from '@mageride/i18n';
import {
  createWwwTranslator,
  DEFAULT_LOCALE,
  HREFLANG,
  isWwwLocale,
  isWwwMessageKey,
  negotiateWwwLocale,
  WWW_LOCALES,
} from '@/i18n';
/*
 * `wwwMessagesFor` moved out of `@/i18n` in S19 and this import is the reason it
 * had to: it asks about `ta`, which is exactly the table no browser should receive.
 * Keeping the total lookup in the module eleven client components import is what
 * put all three tables in the bundle. A test may read it; nothing that renders may.
 */
import { ERROR_STRINGS } from '@/i18n/error-strings';
import { wwwMessagesFor } from '@/i18n/messages/all';
import { wwwEn } from '@/i18n/messages/en';
import { allUrls, ROUTES } from '@/lib/routes';

/**
 * `scripts/check-i18n-parity.mjs` is the gate; this is the part of the same rule
 * that is worth being able to run in isolation while editing a table, plus the
 * behaviour of the translator itself — which the script cannot see, because it
 * reads the tables and never calls anything.
 */

/**
 * The keys whose `si`/`ta` string is byte-identical to `en` **by design**.
 *
 * Module scope rather than block scope because two suites need it: the one that
 * refuses English wearing a translation's clothes, and the Tamil-deferral suite
 * that has to exempt the same six when it checks the `ta` table has not quietly
 * been "finished" by deleting its markers.
 *
 * The assertion using this is exact equality, not a superset. A seventh key going
 * identical is a translation somebody skipped, and it has to be argued for here —
 * with a reason — rather than quietly joining a list nobody rereads.
 */
const IDENTICAL_BY_DESIGN = [
  // A brand name. There is no Sinhala or Tamil MageRide.
  'www.brand.name',
  // A symbol with no letters in it. `isProse` in the shared lint rule draws the
  // same line: "%", "·" and "—" carry no language and are not resources.
  'www.stats.percentSuffix',
  // The language band (S07). These three are *samples*, not translations — the
  // card shows the same sentence in Sinhala, Tamil and English side by side, each
  // in its own `lang` block, so that a reader who only ever sees their own
  // language can see that the app speaks all three. Translating `languageBand.ta`
  // into Sinhala would delete the only thing the card does.
  //
  // They survive the Tamil deferral untouched, and deliberately: the *apps* are
  // trilingual today on all four product surfaces. What MCS-34 D2 defers is this
  // marketing site's own Tamil, which is a different claim from the one this card
  // makes.
  'www.languageBand.si',
  'www.languageBand.ta',
  'www.languageBand.en',
  // "© MageRide" — a symbol and a brand name, and nothing else to translate.
  'www.footer.rights',
];

describe('the three tables', () => {
  it('carry exactly the same keys', () => {
    const expected = Object.keys(wwwEn).sort();
    for (const locale of LOCALES) {
      expect(Object.keys(wwwMessagesFor(locale)).sort(), locale).toEqual(expected);
    }
  });

  it('leave no string empty in any language', () => {
    for (const locale of LOCALES) {
      for (const [key, value] of Object.entries(wwwMessagesFor(locale))) {
        expect(value.trim(), `${locale}: ${key}`).not.toBe('');
      }
    }
  });

  /**
   * MCS-34 D2 defers Tamil to the release after launch. "Deferred" means the table
   * is present and complete, not that it is a copy of English — a `ta` string that
   * is byte-identical to its `en` one is an untranslated key wearing a translation's
   * clothes.
   *
   * **`IDENTICAL_BY_DESIGN` is exhaustive and the assertion is exact equality, not
   * a superset.** That is the point of it: a seventh key going identical is a
   * translation somebody skipped, and it has to be argued for at that constant —
   * with a reason — rather than quietly joining a list nobody rereads. The list
   * grew from one to six in S12, which is the session that first made the si table
   * real; until then every one of them was wearing a `TODO(si)` prefix and so
   * differed from `en` by accident rather than by agreement.
   */
  it('do not pass English off as a translation', () => {
    const untranslated = Object.keys(wwwEn).filter(
      (key) =>
        wwwMessagesFor('si')[key as keyof typeof wwwEn] === wwwEn[key as keyof typeof wwwEn] ||
        wwwMessagesFor('ta')[key as keyof typeof wwwEn] === wwwEn[key as keyof typeof wwwEn],
    );

    expect(untranslated).toEqual(IDENTICAL_BY_DESIGN);
  });

  it('name every route in the table', () => {
    for (const route of ROUTES) {
      expect(isWwwMessageKey(route.labelKey), route.path).toBe(true);
    }
  });
});

describe('the translator', () => {
  it('answers in the locale asked for', () => {
    expect(createWwwTranslator('en')('www.nav.drivers')).toBe('For drivers');
    expect(createWwwTranslator('si')('www.nav.drivers')).not.toBe('For drivers');
  });

  it('falls through to the shared table for a key this surface does not define', () => {
    expect(createWwwTranslator('en')('common.retry')).toBe('Retry');
  });

  /*
   * These two used to exercise `www.scaffold.notice`, which S18 deleted with
   * `StubPage`. `www.screens.filter.showing` replaces it and is a better specimen
   * than the one it replaces: two placeholders rather than one, and **one of them
   * is a number**, so the same assertion now also covers the `Intl.NumberFormat`
   * branch of `createWwwTranslator` that the scaffold notice never reached.
   */
  it('substitutes placeholders', () => {
    expect(
      createWwwTranslator('en')('www.screens.filter.showing', { count: 12, total: 70 }),
    ).toBe('Showing 12 of 70 screens');
  });

  /**
   * A missing value leaves the placeholder visible rather than printing
   * `undefined`: `"{count}"` reaching a reader is a bug somebody reports, whereas
   * "Showing undefined of 70 screens" reads like real copy and ships.
   */
  it('leaves an unsupplied placeholder in the string', () => {
    expect(createWwwTranslator('en')('www.screens.filter.showing', { total: 70 })).toContain(
      '{count}',
    );
  });
});

describe('hreflang', () => {
  it('gives every locale a Sri Lankan region', () => {
    for (const locale of LOCALES) {
      expect(HREFLANG[locale]).toBe(`${locale}-LK`);
    }
  });
});

/**
 * MCS-34 decision **D2** — Tamil ships in the release after launch, not at launch.
 *
 * These cases exist so that the deferral cannot rot in either direction, because
 * both directions are silent failures:
 *
 *   - **Tamil quietly becoming reachable** would serve English prose under
 *     `lang="ta"`. Nothing crashes; a Tamil reader is simply told, in a language
 *     they may not read, that the platform supports them.
 *   - **`ta.ts` quietly decaying** would mean that the day D2 is reversed, "flip
 *     one constant" is a lie and somebody discovers 795 keys of work they thought
 *     was already done.
 *
 * So the deferral is asserted as a *pair*: not published, and still complete.
 * When D2 is reversed, `WWW_LOCALES` becomes `LOCALES` and the three cases below
 * that name Tamil as unpublished are the ones to delete — nothing else.
 */
describe('the generated error-boundary strings (MCS-36 D3)', () => {
  /**
   * **The one client module with no server parent, and this is what keeps it honest.**
   *
   * Every other client component receives resolved strings as props, so the resource
   * tables never cross the boundary. `app/[locale]/error.tsx` cannot — Next
   * instantiates an error boundary itself and hands it no params — so its four strings
   * are generated into `src/i18n/error-strings.ts` by
   * `npm run i18n:error-strings`. Importing the tables for those four would cost
   * **90 kB gzipped on every page**, which is what the measurement showed.
   *
   * A generated file that nothing checks is a stale file waiting to happen: a
   * translator edits `si.ts`, never learns this exists, and the error page keeps the
   * old wording. So every value is asserted against the table it came from.
   */
  it('matches the tables it was generated from', () => {
    for (const locale of WWW_LOCALES) {
      const generated = ERROR_STRINGS[locale];
      expect(generated, `no generated strings for ${locale}`).toBeDefined();

      for (const [key, value] of Object.entries(generated ?? {})) {
        const source = isWwwMessageKey(key)
          ? wwwMessagesFor(locale)[key as keyof typeof wwwEn]
          : createTranslator(locale)(key as MessageKey);

        expect(value, `${locale}: "${key}" is stale — run \`npm run i18n:error-strings\``).toBe(
          source,
        );
      }
    }
  });

  /**
   * And the boundary renders nothing the subset does not carry.
   *
   * The failure this catches is additive rather than stale: somebody adds a fifth
   * string to `error.tsx`, the generator does not know about it, and the page renders
   * an empty `<h1>` in production. Asserted against the source so it cannot depend on
   * rendering a client error boundary in jsdom.
   */
  it('carries every key the error boundary asks for', async () => {
    const source = await readFile(
      resolve(import.meta.dirname, '../app/[locale]/error.tsx'),
      'utf8',
    );
    const asked = [...source.matchAll(/\bt\('([^']+)'\)/g)].map(([, key]) => key);

    expect(asked.length, 'no t() calls found in error.tsx').toBeGreaterThan(0);
    for (const key of asked) {
      expect(Object.keys(ERROR_STRINGS.en ?? {}), `error.tsx renders "${key}"`).toContain(key);
    }
  });
});

describe('the Tamil deferral (MCS-34 D2)', () => {
  it('publishes Sinhala and English, and not Tamil', () => {
    expect([...WWW_LOCALES]).toEqual(['si', 'en']);
  });

  /**
   * The narrowing gate every route runs through. `localeFrom` and the locale
   * layout both call `isWwwLocale`; if either ever reverts to the platform's
   * `isLocale`, `/ta/drivers` starts rendering and this is what says so.
   */
  it('refuses a Tamil URL segment at the routing gate', () => {
    expect(isWwwLocale('ta')).toBe(false);
    expect(isLocale('ta')).toBe(true);

    for (const locale of WWW_LOCALES) {
      expect(isWwwLocale(locale)).toBe(true);
    }
  });

  /**
   * The bug that only fires for the readers it hurts: a Tamil browser hitting the
   * apex would be 307'd to `/ta`, which 404s. It must land on Sinhala instead.
   */
  it('sends a Tamil-only browser to Sinhala rather than to a 404', () => {
    expect(negotiateWwwLocale('ta')).toBe('si');
    expect(negotiateWwwLocale('ta-LK')).toBe('si');
    expect(negotiateWwwLocale('ta-LK,ta;q=0.9')).toBe('si');
    expect(negotiateWwwLocale(null)).toBe(DEFAULT_LOCALE);
    expect(negotiateWwwLocale('')).toBe(DEFAULT_LOCALE);
  });

  /**
   * **The case that distinguishes filtering the header from filtering the answer.**
   * This reader's first choice is Tamil, which this site does not publish, but they
   * have said they also read English. Post-filtering `negotiateLocale`'s result
   * would hand them Sinhala — throwing away a stated preference the site *can*
   * serve. Passing them English is the whole reason `negotiateWwwLocale` strips
   * unpublished tags before ranking rather than after.
   */
  it('honours a lower-ranked language it can serve instead of falling back', () => {
    expect(negotiateWwwLocale('ta-LK,en;q=0.8')).toBe('en');
    expect(negotiateWwwLocale('ta,en-GB;q=0.7,si;q=0.3')).toBe('en');
    expect(negotiateWwwLocale('ta;q=1.0,si;q=0.5')).toBe('si');
  });

  it('still ranks the published locales the way the platform does', () => {
    expect(negotiateWwwLocale('si-LK')).toBe('si');
    expect(negotiateWwwLocale('en-US,si;q=0.9')).toBe('en');
    expect(negotiateWwwLocale('de,fr;q=0.8')).toBe(DEFAULT_LOCALE);

    // The shared negotiator still answers `ta` — this surface's wrapper is what
    // differs, and `@mageride/i18n` must stay untouched for the other three.
    expect(negotiateLocale('ta')).toBe('ta');
    expect(negotiateLocale('ta-LK,en;q=0.8')).toBe('ta');
  });

  it('names no Tamil URL in anything it publishes', () => {
    for (const url of allUrls()) {
      expect(url.startsWith('/ta/') || url === '/ta').toBe(false);
    }
    expect(allUrls()).toContain('/si/drivers');
    expect(allUrls()).toContain('/en/drivers');
  });

  /**
   * The other half of the pair. `ta.ts` is still a complete `WwwMessages` — which
   * the compiler already enforces — and it still carries every one of `en.ts`'s
   * keys with a non-empty string, so the day Tamil ships the only work is the
   * translation itself.
   */
  it('keeps ta.ts structurally complete while it is unpublished', () => {
    const ta = wwwMessagesFor('ta');
    expect(Object.keys(ta).sort()).toEqual(Object.keys(wwwEn).sort());
    for (const [key, value] of Object.entries(ta)) {
      expect(value.trim(), `ta: ${key}`).not.toBe('');
    }
  });

  /**
   * And the debt is still *counted*, not written off. `check-i18n-parity.mjs`
   * reports the marker total on every build; this is the same statement in a form
   * that fails if somebody "tidies up" by deleting the markers and leaving the
   * English behind, which would make the table look finished.
   */
  it('still marks every untranslated Tamil string as untranslated', () => {
    const ta = wwwMessagesFor('ta');
    const pending = Object.entries(ta).filter(([, value]) => value.includes('TODO(ta)'));
    const identicalToEnglish = Object.keys(wwwEn).filter(
      (key) =>
        ta[key as keyof typeof wwwEn] === wwwEn[key as keyof typeof wwwEn] &&
        !IDENTICAL_BY_DESIGN.includes(key),
    );

    expect(pending.length).toBeGreaterThan(0);
    expect(identicalToEnglish).toEqual([]);
  });
});
