/**
 * `@mageride/i18n` — the Si/Ta/En resource scaffolding shared by the Admin
 * Portal, the Fleet Portal and the passenger web subview.
 *
 * "Trilingual resources. All user-facing strings must support Si (Sinhala),
 * Ta (Tamil), En (English). Use resource files, never hardcode strings."
 * — CLAUDE.md, Universal Rules. D2 §AP/§FP repeat it per surface.
 *
 * Two halves enforce that:
 *   - here, the type system — `si.ts`/`ta.ts` are `Messages`, so no key can
 *     exist in one language and not the others;
 *   - in `@mageride/eslint-config`, `mageride/no-literal-user-facing-strings`,
 *     which stops a string being written into JSX instead of a resource file.
 *
 * Deliberately dependency-free and framework-free: it is consumed by React
 * server components, client components and plain modules alike, so it must not
 * assume any of them.
 */

import { en, type MessageKey, type Messages } from './locales/en.js';
import { si } from './locales/si.js';
import { ta } from './locales/ta.js';

export type { MessageKey, Messages };
export { en, si, ta };

/** The three languages the platform supports. */
export type Locale = 'si' | 'ta' | 'en';

/**
 * D1' §283 / D2 SCR-*-002: "vertical Si/Ta/En (Sinhala first & default)".
 * The order is the display order too — a language picker renders `LOCALES`
 * as-is rather than sorting it.
 */
export const LOCALES: readonly Locale[] = ['si', 'ta', 'en'] as const;

/**
 * The apps open in Sinhala (D1' §283). The web surfaces have no stated default,
 * so they take the same one — a Sri Lankan platform whose apps are Sinhala-first
 * should not become English-first because the surface happens to be a browser.
 * A surface that knows better (a signed-in staff member's stored preference,
 * an `Accept-Language` header) passes its own locale to `createTranslator`.
 */
export const DEFAULT_LOCALE: Locale = 'si';

/**
 * Where a lookup goes when a key is missing. It should never fire — `Messages`
 * makes an incomplete locale a compile error — but resources can also arrive at
 * runtime (a CMS string, a server-rendered payload), and a missing translation
 * must degrade to readable text rather than to the raw key.
 */
export const FALLBACK_LOCALE: Locale = 'en';

const RESOURCES: Readonly<Record<Locale, Messages>> = { si, ta, en };

/** Values a placeholder may take. Numbers are formatted for the locale. */
export type MessageParams = Readonly<Record<string, string | number>>;

/** Looks up a message and substitutes its `{placeholders}`. */
export type Translator = (key: MessageKey, params?: MessageParams) => string;

export function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && (LOCALES as readonly string[]).includes(value);
}

/** The resource table for a locale. */
export function messagesFor(locale: Locale): Messages {
  return RESOURCES[locale];
}

/**
 * Picks the best supported locale from an `Accept-Language` header, honouring
 * quality values. Falls back to {@link DEFAULT_LOCALE}. Matching is on the
 * primary subtag, so `si-LK` and `ta-LK` both land where they should.
 */
export function negotiateLocale(acceptLanguage: string | null | undefined): Locale {
  if (!acceptLanguage) return DEFAULT_LOCALE;

  const ranked = acceptLanguage
    .split(',')
    .map((part) => {
      const [tag = '', ...rest] = part.trim().split(';');
      const q = rest.find((p) => p.trim().startsWith('q='));
      const quality = q ? Number.parseFloat(q.trim().slice(2)) : 1;
      return { tag: tag.trim().toLowerCase(), quality: Number.isFinite(quality) ? quality : 0 };
    })
    .filter((entry) => entry.tag && entry.quality > 0)
    .sort((a, b) => b.quality - a.quality);

  for (const { tag } of ranked) {
    const primary = tag.split('-')[0];
    if (isLocale(primary)) return primary;
  }
  return DEFAULT_LOCALE;
}

const PLACEHOLDER = /\{(\w+)\}/g;

/**
 * Builds the translator for a locale.
 *
 * A missing placeholder value is left in the string rather than replaced with
 * `undefined`: `"{count} selected"` reaching a user unsubstituted is a visible
 * bug someone reports, whereas `"undefined selected"` reads like real copy.
 */
export function createTranslator(locale: Locale = DEFAULT_LOCALE): Translator {
  const primary = messagesFor(locale);
  const fallback = messagesFor(FALLBACK_LOCALE);
  const numberFormat = new Intl.NumberFormat(`${locale}-LK`);

  return function t(key, params) {
    const template = primary[key] ?? fallback[key] ?? key;
    if (!params) return template;

    return template.replace(PLACEHOLDER, (match, name: string) => {
      const value = params[name];
      if (value === undefined) return match;
      return typeof value === 'number' ? numberFormat.format(value) : value;
    });
  };
}
