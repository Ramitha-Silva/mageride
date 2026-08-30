import { redirect } from 'next/navigation';

import { getNegotiatedLocale } from '@/i18n/server';
import { href } from '@/lib/routes';

/**
 * `/` — the one address on this site that is not a page.
 *
 * There is no locale-less reading of any document here: `/drivers` does not exist,
 * only `/si/drivers`, `/ta/drivers` and `/en/drivers` do, and each is canonical
 * (A32). So the bare host negotiates a language from `Accept-Language` and sends
 * the reader to the corresponding home. Sinhala when the header says nothing —
 * D1' §283 makes the platform Sinhala-first, and a Sri Lankan visitor arriving at
 * `www.mageride.lk` should not get English because the surface happens to be a
 * browser.
 *
 * **A 307, not a 308.** `redirect()`'s default is temporary on purpose: the target
 * depends on the request's own `Accept-Language`, so it must not be cached by an
 * intermediary and replayed at a reader whose browser asks for a different
 * language. It is also the only dynamic response this site produces — every other
 * URL is static, which is what lets the site serve with the platform down.
 *
 * The apex `mageride.lk` → `www.mageride.lk` **301** is a different redirect,
 * belongs to the ingress and arrives in S21 (A43).
 */
export default async function RootPage() {
  const locale = await getNegotiatedLocale();
  redirect(href(locale, ''));
}
