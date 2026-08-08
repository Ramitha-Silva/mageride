/**
 * The Admin Portal's translator: this surface's own resources over
 * `@mageride/i18n`'s shared ones, resolved through one function.
 *
 * Two tables rather than one because they have two owners. The shared package
 * "carries only what every surface shares" (its CLAUDE.md) — `common.cancel`,
 * `status.approved`, the language names — and everything under `admin.*` and
 * `nav.*` is this component's, translated in `./messages/{en,si,ta}.ts` and
 * type-checked against each other so no key can exist in one language and not
 * the others.
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

import { adminEn, type AdminMessageKey, type AdminMessages } from './messages/en';
import { adminSi } from './messages/si';
import { adminTa } from './messages/ta';

export { DEFAULT_LOCALE, FALLBACK_LOCALE, isLocale, LOCALES, negotiateLocale };
export type { Locale, MessageParams };
export type { AdminMessageKey };

/** Any key the portal can render — this surface's, or the shared set's. */
export type AnyMessageKey = AdminMessageKey | MessageKey;

export type AdminTranslator = (key: AnyMessageKey, params?: MessageParams) => string;

const ADMIN_RESOURCES: Readonly<Record<Locale, AdminMessages>> = {
  si: adminSi,
  ta: adminTa,
  en: adminEn,
};

const PLACEHOLDER = /\{(\w+)\}/g;

/** Whether a string is one of this surface's keys. */
export function isAdminMessageKey(key: string): key is AdminMessageKey {
  return Object.hasOwn(adminEn, key);
}

/**
 * Builds the translator for a locale.
 *
 * A missing placeholder value is left in the string rather than replaced with
 * `undefined` — `"{minutes} minutes"` reaching an operator unsubstituted is a
 * visible bug somebody reports, whereas "undefined minutes" reads like real copy.
 * The same rule as `@mageride/i18n`'s own translator, for the same reason.
 */
export function createAdminTranslator(locale: Locale = DEFAULT_LOCALE): AdminTranslator {
  const shared = createTranslator(locale);
  const primary = ADMIN_RESOURCES[locale];
  const fallback = ADMIN_RESOURCES[FALLBACK_LOCALE];
  const numberFormat = new Intl.NumberFormat(`${locale}-LK`);

  return function t(key, params) {
    if (!isAdminMessageKey(key)) return shared(key, params);

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
 * Renders a resource key the **server** chose — every `AdminMenuGroup.labelKey`
 * and `AdminMenuItem.labelKey` on `GET /v1/admin/session`.
 *
 * Those arrive as plain strings, so they cannot be typed at the call site, and a
 * key this build has never heard of is possible the moment admin-bff adds a nav
 * item ahead of the portal. It falls back to the key itself, which renders as
 * `nav.somethingNew` in the sidebar: ugly, obviously wrong, and reported — where
 * an empty label would be a nav entry nobody can see or click.
 * `test/nav-labels.test.ts` is what stops it happening in the first place.
 */
export function translateServerKey(
  t: AdminTranslator,
  key: string,
  params?: MessageParams,
): string {
  return isAdminMessageKey(key) ? t(key, params) : key;
}
