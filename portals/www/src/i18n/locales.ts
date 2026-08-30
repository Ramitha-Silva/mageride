import { DEFAULT_LOCALE, isLocale, negotiateLocale, type Locale } from '@mageride/i18n';

/**
 * **The published locale set and its BCP-47 tags — and this module exists because it
 * imports no message table.**
 *
 * ## Why it is separate from `./index.ts`
 *
 * `./index.ts` holds `createWwwTranslator`, which needs the tables, so importing
 * *anything* from it drags ~88 kB gzipped of resources into whatever imported it.
 * That was fine while only server components read it. It stopped being fine when
 * `app/[locale]/error.tsx` — a **client** error boundary that Next hands no params,
 * so it has to know the locale list at module scope — imported `WWW_LOCALES` from
 * there and pulled both tables into the client bundle with it.
 *
 * So the constants that are *facts about which locales exist* live here, where they
 * cost a few hundred bytes, and the translator stays in `./index.ts`. A client
 * component may import from this module freely; importing the translator is what
 * `test/fences.test.ts` refuses (MCS-36 **D3**).
 *
 * ## The derivation inverted, and the invariant kept
 *
 * S19 had `WWW_LOCALES = Object.keys(WWW_PUBLISHED_MESSAGES)` so that publishing a
 * locale and shipping its table were one decision — *a locale's table reaches a
 * browser if and only if the locale is published*. That derivation cannot survive
 * the split: the list has to exist without the tables.
 *
 * It is inverted rather than abandoned. This is now the declaration, and
 * `./index.ts` builds its table map `satisfies Record<WwwLocale, WwwMessages>` —
 * so a published locale with no table is a **compile error**, which is a stronger
 * guarantee than the runtime derivation it replaces. `test/i18n.test.ts` holds the
 * other direction.
 */

/**
 * MCS-34 **D2**: si + en complete first, Tamil next release.
 *
 * The reasoning belongs beside the constant rather than in a document. There is no
 * native Tamil reviewer identified anywhere in this repo, and ~21k words of
 * machine-translated Tamil under a Tamil label is worse for a Tamil reader than no
 * Tamil at all — it tells them the platform does not really support them, and it
 * tells them so in their own language.
 *
 * **Deferring is not "leave the TODOs in and hope".** `ta.ts` stays a complete,
 * type-checked table — a key added to `en.ts` without a Tamil string is still a
 * compile error, and `check-i18n-parity.mjs` still counts its outstanding markers on
 * every build. What this constant removes is not the *table* but the *routes*:
 * `/ta/anything` is not published, not in the sitemap, not in any `hreflang` set,
 * and answers **404**.
 *
 * **It must 404 rather than serve English under `lang="ta"`.** A wrong `lang` is an
 * accessibility failure, not a cosmetic one: a screen reader hands English prose to
 * a Tamil speech engine and produces sounds that are not words in any language.
 *
 * ## Re-enabling Tamil
 *
 * Add the locale to this tuple, then add its table to `WWW_PUBLISHED_MESSAGES` in
 * `./index.ts` — the second is a compile error until it is done, which is the point
 * of the `satisfies` there. Two adjacent edits, both refusing to be forgotten.
 *
 * (Written out rather than shown as a snippet: `test/fences.test.ts` asserts by text
 * that `./index.ts` imports no unpublished table, and a sample import in a comment
 * about adding one trips it. CLAUDE.md's rule — *describe the rule, do not spell
 * it* — and this surface has now met it four times.)
 *
 * **`@mageride/i18n`'s `LOCALES` is deliberately untouched and must stay so.** Three
 * other surfaces read it and Tamil is deferred on none of them; the apps are
 * trilingual today. This deferral is the marketing site's alone.
 */
const PUBLISHED = ['si', 'en'] as const satisfies readonly Locale[];

/**
 * A locale this surface actually publishes. Narrower than `@mageride/i18n`'s
 * `Locale`, and its only job is to make the table map in `./index.ts` a compile-time
 * check — `Record<WwwLocale, WwwMessages>` cannot be satisfied by a map missing one.
 */
export type WwwLocale = (typeof PUBLISHED)[number];

/**
 * The published set, **widened back to `Locale[]` on purpose.**
 *
 * The tuple above is narrow so the type can do work; this is what every caller
 * iterates, and it stays `readonly Locale[]` because the callers hold a `Locale` —
 * `WWW_LOCALES.includes(someLocale)` and `LOCALES.filter((l) => !WWW_LOCALES.includes(l))`
 * are both about "is this platform locale one we publish", and against a narrow tuple
 * TypeScript rejects the question rather than answering it. Narrowing here would push
 * a cast into every call site, which is the opposite of the guarantee.
 */
export const WWW_LOCALES: readonly Locale[] = PUBLISHED;

/**
 * Whether a URL segment is a locale **this surface publishes**.
 *
 * Deliberately not `isLocale`, which is the platform's question and answers `true`
 * for `ta` — a segment this site does not serve.
 */
export function isWwwLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (WWW_LOCALES as readonly string[]).includes(value);
}

/**
 * The BCP-47 tag each locale is published under.
 *
 * Separate from `Locale` because the two are used for different things and only one
 * of them is a URL segment. `hreflang` and `<html lang>` want a region — a search
 * engine distinguishing `ta-LK` from `ta-IN` is exactly the point of the annotation
 * (A32) — while the path stays the bare two-letter code so that `/si/drivers` is
 * short enough to say out loud.
 *
 * **Total over `Locale`, including the deferred one.** This is a lookup table, not a
 * published set: which tags are actually emitted follows {@link WWW_LOCALES} and
 * every consumer iterates that. Keeping `ta` here means re-enabling Tamil does not
 * also have to remember to put its tag back.
 */
export const HREFLANG: Readonly<Record<Locale, string>> = {
  si: 'si-LK',
  ta: 'ta-LK',
  en: 'en-LK',
};

/**
 * Negotiate against **the published set**, by filtering the header before ranking it
 * rather than correcting the answer afterwards.
 *
 * Post-filtering looks equivalent and silently loses a reader: on
 * `Accept-Language: ta-LK, en;q=0.8` it serves Sinhala to somebody who said they read
 * English. Stripping unpublished tags first means that reader gets English, and a
 * Tamil-only reader gets Sinhala.
 */
export function negotiateWwwLocale(acceptLanguage: string | null | undefined): Locale {
  if (!acceptLanguage) return DEFAULT_LOCALE;

  const published = acceptLanguage
    .split(',')
    .filter((part) => {
      const [tag = ''] = part.trim().split(';');
      return isWwwLocale(tag.trim().toLowerCase().split('-')[0]);
    })
    .join(',');

  return published ? negotiateLocale(published) : DEFAULT_LOCALE;
}

export { DEFAULT_LOCALE, isLocale };
export type { Locale };
