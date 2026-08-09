import 'server-only';

import { headers } from 'next/headers';

import { appDownloadUrl } from '@/config/env';
import { translatorFor } from '@/i18n/server';
import { localeFor, LOCALE_PARAM, type Locale, type WebTranslator } from '@/i18n';

/**
 * The four things every one of the six screens needs before it can render a single
 * word, resolved once.
 *
 * Deliberately **not** a layout. An App Router layout is reused, not re-rendered,
 * when navigation moves between its children — and this surface's language comes
 * off the query string, so a layout that resolved it would resolve it on the first
 * page load and never again, which is exactly the navigation the `?lang=` switch
 * makes.
 */

export interface ScreenContext {
  readonly locale: Locale;
  readonly t: WebTranslator;
  /** This URL without its `?lang=`, so the switch can put a different one on. */
  readonly here: string;
  /** The store link for the phone that is asking, or `null` when none is configured. */
  readonly appUrl: string | null;
}

/** Next's own `searchParams` shape. */
export type SearchParams = Record<string, string | string[] | undefined>;

export async function screenContext(
  pathname: string,
  searchParams: SearchParams,
): Promise<ScreenContext> {
  const requestHeaders = await headers();
  const locale = localeFor(searchParams[LOCALE_PARAM], requestHeaders.get('accept-language'));

  return {
    locale,
    t: translatorFor(locale),
    here: hereWithout(pathname, searchParams),
    appUrl: appDownloadUrl(requestHeaders.get('user-agent')),
  };
}

/**
 * The current URL with `?lang=` removed and everything else kept.
 *
 * The language switch appends its own, so leaving the old one on would produce
 * `?token=…&lang=si&lang=ta` — which `localeFor` reads as the *first* value, i.e.
 * the language the reader has just switched away from.
 */
export function hereWithout(pathname: string, searchParams: SearchParams): string {
  const query = new URLSearchParams();

  for (const [key, value] of Object.entries(searchParams)) {
    if (key === LOCALE_PARAM || value === undefined) continue;
    for (const one of Array.isArray(value) ? value : [value]) query.append(key, one);
  }

  const suffix = query.toString();
  return suffix ? `${pathname}?${suffix}` : pathname;
}
