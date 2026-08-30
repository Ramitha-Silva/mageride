import 'server-only';

import { cache } from 'react';
import { headers } from 'next/headers';

import { DEFAULT_LOCALE, negotiateWwwLocale, type Locale } from './index';

/**
 * The request-shaped half of language resolution, and the *only* part of this
 * surface that reads a request at all.
 *
 * The rule itself lives in `@mageride/i18n`'s `negotiateLocale`, which is pure and
 * tested there. What lives here is the part that needs a request: reading the
 * header, and doing it once per render.
 *
 * **Used by exactly one route — `app/page.tsx`, the bare `/` redirect.** Every
 * other page on this site takes its locale from its own `[locale]` path segment
 * and never asks a header, which is what makes those pages statically renderable
 * and cacheable at the edge. A page that called this would opt itself out of that
 * for a value its own URL already carries.
 *
 * `cache()`d so a render that resolves the locale twice reads the header once.
 *
 * **`negotiateWwwLocale` and not the shared `negotiateLocale`** (S13). The shared
 * one answers `ta` for a Tamil browser, and while MCS-34 D2's deferral stands `/`
 * would then 307 that reader straight into a 404 — the deferral's most likely bug,
 * and its least visible, because it fires only for the readers it hurts. Tamil
 * falls through to Sinhala, as an unrecognised language already does.
 */
export const getNegotiatedLocale = cache(async (): Promise<Locale> => {
  const accept = (await headers()).get('accept-language');
  return accept ? negotiateWwwLocale(accept) : DEFAULT_LOCALE;
});
