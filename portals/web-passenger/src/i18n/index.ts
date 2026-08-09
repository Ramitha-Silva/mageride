/**
 * The subview's translator: this surface's own resources over `@mageride/i18n`'s
 * shared ones, resolved through one function.
 *
 * Two tables rather than one because they have two owners. The shared package
 * "carries only what every surface shares" (its CLAUDE.md) — `common.retry`, the
 * language names — and everything under `web.*` is this component's, translated in
 * `./messages/{en,si,ta}.ts` and type-checked against each other so no key can
 * exist in one language and not the others.
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

import { webEn, type WebMessageKey, type WebMessages } from './messages/en';
import { webSi } from './messages/si';
import { webTa } from './messages/ta';

export { DEFAULT_LOCALE, FALLBACK_LOCALE, isLocale, LOCALES, negotiateLocale };
export type { Locale, MessageParams };
export type { WebMessageKey };

/** Any key this surface can render — its own, or the shared set's. */
export type AnyMessageKey = WebMessageKey | MessageKey;

export type WebTranslator = (key: AnyMessageKey, params?: MessageParams) => string;

const WEB_RESOURCES: Readonly<Record<Locale, WebMessages>> = {
  si: webSi,
  ta: webTa,
  en: webEn,
};

const PLACEHOLDER = /\{(\w+)\}/g;

/** Whether a string is one of this surface's keys. */
export function isWebMessageKey(key: string): key is WebMessageKey {
  return Object.hasOwn(webEn, key);
}

/**
 * Builds the translator for a locale.
 *
 * A missing placeholder value is left in the string rather than replaced with
 * `undefined` — `"{minutes} min"` reaching a reader unsubstituted is a visible bug
 * somebody reports, whereas "undefined min" reads like real copy. The same rule as
 * `@mageride/i18n`'s own translator, for the same reason.
 */
export function createWebTranslator(locale: Locale = DEFAULT_LOCALE): WebTranslator {
  const shared = createTranslator(locale);
  const primary = WEB_RESOURCES[locale];
  const fallback = WEB_RESOURCES[FALLBACK_LOCALE];
  const numberFormat = new Intl.NumberFormat(`${locale}-LK`);

  return function t(key, params) {
    if (!isWebMessageKey(key)) return shared(key, params);

    const template = primary[key] ?? fallback[key] ?? key;
    if (!params) return template;

    return template.replace(PLACEHOLDER, (match, name: string) => {
      const value = params[name];
      if (value === undefined) return match;
      return typeof value === 'number' ? numberFormat.format(value) : value;
    });
  };
}

/**
 * The `?lang=` switch's parameter name.
 *
 * **The language is a query parameter and not a cookie**, which is the one place
 * this surface diverges from the two operator consoles. D6' I-29.1 is explicit
 * that the subview keeps "no cookies, no localStorage of ride data" and describes
 * a CDN-cacheable static shell; a preference cookie would be a state this page is
 * not supposed to have, set on a device that belongs to somebody with no MageRide
 * account. A parameter costs nothing, survives a reload, and travels with the link
 * if the reader forwards it.
 *
 * Not a `/[locale]/…` segment either: the SMS carries one URL, minted by
 * notification-svc before anybody knew what language the recipient reads.
 */
export const LOCALE_PARAM = 'lang';

/**
 * The locale a request renders in, given the `?lang=` it carried (if any) and the
 * browser's `Accept-Language`.
 *
 * Two sources and no third. The other two portals read the signed-in member's
 * stored `iam.users.language`; **nobody is signed in here**, and the token's scope
 * is not a person — resolving a language off the ride would mean reading the
 * *booker's* preference to somebody who is not the booker.
 *
 * With neither, `si`: D1' §283 makes the platform Sinhala-first, and a page a Sri
 * Lankan recipient opens from an SMS should not be English because the surface
 * happens to be a browser.
 *
 * Pure, and here rather than in `./server` on purpose — it is the one piece of
 * language resolution with no request object in it, so it is the piece a test can
 * hold to the rule.
 */
export function localeFor(
  requested: string | string[] | undefined,
  acceptLanguage: string | null,
): Locale {
  const chosen = Array.isArray(requested) ? requested[0] : requested;
  if (isLocale(chosen)) return chosen;

  return acceptLanguage ? negotiateLocale(acceptLanguage) : DEFAULT_LOCALE;
}
