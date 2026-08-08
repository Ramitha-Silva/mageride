import 'server-only';

import { cache } from 'react';
import { cookies, headers } from 'next/headers';

import { LOCALE_COOKIE, THEME_COOKIE } from '@/server/cookies';
import { displayIdentity } from '@/server/session';

import {
  createFleetTranslator,
  DEFAULT_LOCALE,
  isLocale,
  negotiateLocale,
  type FleetTranslator,
  type Locale,
} from './index';

/**
 * Which language and which appearance this request renders in.
 *
 * ## Language (D-26, D1' §283)
 *
 * Four sources, most specific first:
 *
 *  1. the **language switcher's cookie** — an explicit choice on this browser;
 *  2. the member's **stored account language**, which is what they picked on
 *     whichever MageRide surface they last set it (`iam.users.language`);
 *  3. the browser's **`Accept-Language`**, negotiated on the primary subtag so
 *     `si-LK` and `ta-LK` both land where they should;
 *  4. `si`, because D1' §283 makes the platform Sinhala-first and a Sri Lankan
 *     fleet console should not become English-first for being in a browser.
 *
 * Deliberately not a `/[locale]/…` route segment. The language is a property of
 * the person, not of the resource, and a locale in the path would make every
 * bookmark, every deep link pasted into a message, and every `next=` redirect
 * carry a language somebody else then reads the page in.
 *
 * ## Appearance
 *
 * `light` | `dark` | `system`, and `system` is the default. The class is applied
 * server-side from the cookie so there is no flash of the wrong theme; `system`
 * is finished off by the small bootstrap script in the root layout, which is the
 * one thing a server cannot know before the first paint.
 */

export type Appearance = 'light' | 'dark' | 'system';

export const getLocale = cache(async (): Promise<Locale> => {
  const chosen = (await cookies()).get(LOCALE_COOKIE)?.value;
  if (isLocale(chosen)) return chosen;

  const stored = (await displayIdentity())?.language;
  if (isLocale(stored)) return stored;

  const accept = (await headers()).get('accept-language');
  return accept ? negotiateLocale(accept) : DEFAULT_LOCALE;
});

/** The translator for this request. `cache()`d so one render resolves one locale. */
export const getTranslator = cache(async (): Promise<FleetTranslator> => {
  return createFleetTranslator(await getLocale());
});

export async function getAppearance(): Promise<Appearance> {
  const value = (await cookies()).get(THEME_COOKIE)?.value;
  return value === 'light' || value === 'dark' ? value : 'system';
}
