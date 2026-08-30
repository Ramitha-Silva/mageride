import { notFound } from 'next/navigation';

import { isWwwLocale, type Locale } from '@/i18n';

/**
 * The shape Next hands every page below `app/[locale]/`.
 *
 * `locale` is a `string` and not a `Locale` because that is what the router knows:
 * the type has to match the one Next generates into `.next/types`, and Next has no
 * way to express "this segment is one of three values". Narrowing it is
 * {@link localeFrom}'s job.
 */
export interface LocaleParams {
  locale: string;
}

/**
 * The locale a page renders in, narrowed once.
 *
 * `app/[locale]/layout.tsx` sets `dynamicParams = false`, so in production an
 * unknown segment is refused by the router before any of this runs. This is the
 * same statement for everything the router does not gate — the dev server, a
 * direct call from a test — and it is here rather than repeated in thirteen pages
 * so that "an unknown locale is a 404, never a fallback to Sinhala" is written
 * down once.
 *
 * Falling back would be the tempting mistake: it would answer `/de/drivers` with
 * the Sinhala page, which gives a crawler a second URL for a document that already
 * has a canonical one and quietly breaks the reciprocal `hreflang` set (A32).
 *
 * **The test is `isWwwLocale`, not `isLocale` (S13).** `ta` is a platform locale
 * and is not a locale this surface publishes — MCS-34 D2 deferred it — so
 * `/ta/drivers` takes the same path as `/de/drivers` and 404s. Serving the English
 * page under `lang="ta"` instead would be an accessibility failure rather than a
 * cosmetic one.
 */
export async function localeFrom(params: Promise<LocaleParams>): Promise<Locale> {
  const { locale } = await params;
  if (!isWwwLocale(locale)) notFound();
  return locale;
}
