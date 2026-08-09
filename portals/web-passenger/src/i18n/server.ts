import 'server-only';

import { cache } from 'react';
import { headers } from 'next/headers';

import {
  createWebTranslator,
  DEFAULT_LOCALE,
  negotiateLocale,
  type Locale,
  type WebTranslator,
} from './index';

/**
 * The request-shaped half of language resolution.
 *
 * The rule itself — `?lang=` first, then `Accept-Language`, then Sinhala — is
 * `localeFor` in `./index`, where it is a pure function a test can hold to D1'
 * §283. What lives here is only the part that needs a request: reading the header,
 * and doing it once per render.
 */

/**
 * The negotiated locale for this request, ignoring `?lang=`.
 *
 * Used by the root layout and by the three `loading.tsx` gates, which render before
 * any page has parsed its own search params. A page that knows better calls
 * `localeFor` and passes the result down; the two agree except in the one render
 * where the reader has just switched language, and `<html lang>` is corrected on
 * the next navigation.
 */
export const getNegotiatedLocale = cache(async (): Promise<Locale> => {
  const accept = (await headers()).get('accept-language');
  return accept ? negotiateLocale(accept) : DEFAULT_LOCALE;
});

/** The translator for a locale. Screens resolve their own; nothing is global. */
export function translatorFor(locale: Locale): WebTranslator {
  return createWebTranslator(locale);
}
