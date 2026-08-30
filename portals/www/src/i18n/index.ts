/**
 * The site's translator: this surface's own resources over `@mageride/i18n`'s
 * shared ones, resolved through one function.
 *
 * Two tables rather than one because they have two owners. The shared package
 * "carries only what every surface shares" (its CLAUDE.md) — `common.retry`, the
 * language names — and everything under `www.*` is this component's, translated in
 * `./messages/{en,si,ta}.ts` and type-checked against each other so no key can
 * exist in one language and not the others (README §4.4).
 */

import {
  createTranslator,
  DEFAULT_LOCALE,
  FALLBACK_LOCALE,
  isLocale,
  LOCALES,
  negotiateLocale,
  type Locale,
  type MessageKey,
  type MessageParams,
} from '@mageride/i18n';

import type { WwwLocale } from './locales';
import { wwwEn, type WwwMessageKey, type WwwMessages } from './messages/en';
import { wwwSi } from './messages/si';
// The deferred locale's table is deliberately not imported here, and its absence is
// load-bearing rather than an oversight. Eleven client components import this
// module, so every table named above is downloaded by every reader — see
// `./messages/all`, which is where the total lookup lives and why.


export { DEFAULT_LOCALE, FALLBACK_LOCALE, isLocale, LOCALES, negotiateLocale };
export type { Locale, MessageParams };
export type { WwwMessageKey };

/**
 * The tables this surface publishes — **and `satisfies` is what keeps the invariant
 * S19 built, now that `WWW_LOCALES` no longer derives from this map.**
 *
 * S19 had it the other way round: this map was the declaration and the locale list
 * was `Object.keys` of it, so publishing a locale and shipping its table were one
 * act. **MCS-36 D3's split makes that impossible** — `./locales` has to state the
 * list without importing a table, because a client component needs the list and must
 * not have the tables. So the direction inverted, and the guarantee got *stronger*:
 * `Record<WwwLocale, …>` makes a published locale with no table a **compile error**,
 * where the old form could only be wrong at runtime.
 *
 * The other direction — a table here for a locale nobody publishes — is
 * `test/fences.test.ts`, by text, because an unused import is not a type error.
 */
const WWW_PUBLISHED_MESSAGES = {
  si: wwwSi,
  en: wwwEn,
} as const satisfies Record<WwwLocale, WwwMessages>;

/**
 * The published set, its tags and its two helpers live in `./locales` and are
 * re-exported here so no server caller has to know that.
 *
 * **Why they moved: MCS-36 D3.** Everything else in this file needs the message
 * tables — that is what a translator is — so importing *anything* from here drags
 * ~88 kB gzipped into the importer. `app/[locale]/error.tsx` is a **client** error
 * boundary that Next hands no params, so it has to know the locale list at module
 * scope; importing `WWW_LOCALES` from here put both tables in the client bundle.
 * Which locales exist is not the translator's business, so it moved somewhere that
 * costs a few hundred bytes.
 *
 * **A client component imports `@/i18n/locales` directly.** `test/fences.test.ts`
 * refuses a client import of *this* module; these re-exports are for server code,
 * where one import path is kinder than two.
 */
export {
  HREFLANG,
  isWwwLocale,
  negotiateWwwLocale,
  WWW_LOCALES,
  type WwwLocale,
} from './locales';

/** Any key this surface can render — its own, or the shared set's. */
export type AnyMessageKey = WwwMessageKey | MessageKey;

export type WwwTranslator = (key: AnyMessageKey, params?: MessageParams) => string;

/**
 * Published tables only, keyed by locale.
 *
 * An unpublished locale resolves to `undefined` here, which is why every read below
 * falls back to {@link FALLBACK_LOCALE}. That path is unreachable in the app —
 * `dynamicParams = false` and `localeFrom` both refuse an unpublished segment, and
 * the two pages that render several languages at once iterate `WWW_LOCALES` — so
 * the fallback is a floor rather than a feature. English beats a crash on the one
 * page that has already failed the reader.
 */
const WWW_RESOURCES: Partial<Readonly<Record<Locale, WwwMessages>>> = WWW_PUBLISHED_MESSAGES;

const PLACEHOLDER = /\{(\w+)\}/g;

/** Whether a string is one of this surface's keys. */
export function isWwwMessageKey(key: string): key is WwwMessageKey {
  return Object.hasOwn(wwwEn, key);
}

/**
 * Builds the translator for a locale.
 *
 * A missing placeholder value is left in the string rather than replaced with
 * `undefined` — `"{route} is being written"` reaching a reader unsubstituted is a
 * visible bug somebody reports, whereas "undefined is being written" reads like
 * real copy. The same rule as `@mageride/i18n`'s own translator, for the same
 * reason.
 */
export function createWwwTranslator(locale: Locale = DEFAULT_LOCALE): WwwTranslator {
  const shared = createTranslator(locale);
  const fallback = WWW_RESOURCES[FALLBACK_LOCALE] ?? wwwEn;
  const primary = WWW_RESOURCES[locale] ?? fallback;
  const numberFormat = new Intl.NumberFormat(`${locale}-LK`);

  return function t(key, params) {
    if (!isWwwMessageKey(key)) return shared(key, params);

    const template = primary[key] ?? fallback[key] ?? key;
    if (!params) return template;

    return template.replace(PLACEHOLDER, (match, name: string) => {
      const value = params[name];
      if (value === undefined) return match;
      return typeof value === 'number' ? numberFormat.format(value) : value;
    });
  };
}

