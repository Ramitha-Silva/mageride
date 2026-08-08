/**
 * The Fleet Portal's translator: this surface's own resources over
 * `@mageride/i18n`'s shared ones, resolved through one function.
 *
 * Two tables rather than one because they have two owners. The shared package
 * "carries only what every surface shares" (its CLAUDE.md) — `common.cancel`,
 * `status.approved`, the language names — and everything under `fleet.*` is this
 * component's, translated in `./messages/{en,si,ta}.ts` and type-checked against
 * each other so no key can exist in one language and not the others.
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

import { fleetEn, type FleetMessageKey, type FleetMessages } from './messages/en';
import { fleetSi } from './messages/si';
import { fleetTa } from './messages/ta';

export { DEFAULT_LOCALE, FALLBACK_LOCALE, isLocale, LOCALES, negotiateLocale };
export type { Locale, MessageParams };
export type { FleetMessageKey };

/** Any key the portal can render — this surface's, or the shared set's. */
export type AnyMessageKey = FleetMessageKey | MessageKey;

export type FleetTranslator = (key: AnyMessageKey, params?: MessageParams) => string;

const FLEET_RESOURCES: Readonly<Record<Locale, FleetMessages>> = {
  si: fleetSi,
  ta: fleetTa,
  en: fleetEn,
};

const PLACEHOLDER = /\{(\w+)\}/g;

/** Whether a string is one of this surface's keys. */
export function isFleetMessageKey(key: string): key is FleetMessageKey {
  return Object.hasOwn(fleetEn, key);
}

/**
 * Builds the translator for a locale.
 *
 * A missing placeholder value is left in the string rather than replaced with
 * `undefined` — `"{minutes} minutes"` reaching an operator unsubstituted is a
 * visible bug somebody reports, whereas "undefined minutes" reads like real copy.
 * The same rule as `@mageride/i18n`'s own translator, for the same reason.
 */
export function createFleetTranslator(locale: Locale = DEFAULT_LOCALE): FleetTranslator {
  const shared = createTranslator(locale);
  const primary = FLEET_RESOURCES[locale];
  const fallback = FLEET_RESOURCES[FALLBACK_LOCALE];
  const numberFormat = new Intl.NumberFormat(`${locale}-LK`);

  return function t(key, params) {
    if (!isFleetMessageKey(key)) return shared(key, params);

    const template = primary[key] ?? fallback[key] ?? key;
    if (!params) return template;

    return template.replace(PLACEHOLDER, (match, name: string) => {
      const value = params[name];
      if (value === undefined) return match;
      return typeof value === 'number' ? numberFormat.format(value) : value;
    });
  };
}
