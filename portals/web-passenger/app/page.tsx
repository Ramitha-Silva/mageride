import { DeadEnd } from '@/components/DeadEnd';
import { screenContext, type SearchParams } from '@/server/render';

/**
 * `passenger.mageride.lk/` with no token on it.
 *
 * **SCR-WT-006, not a home page.** There is no product at this address: every URL
 * on this host is somebody's live share token, and the only way to arrive here
 * without one is to have typed the host, followed a truncated SMS, or opened a link
 * whose query string a messaging app ate. All three are "this link does not work",
 * which is the page this surface already has.
 *
 * A marketing page here would also be the one page on the host a crawler could
 * legitimately index, which is an invitation to look at the rest of it.
 */
export const dynamic = 'force-dynamic';

export default async function RootPage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  const params = await searchParams;
  const { locale, t, here, appUrl } = await screenContext('/', params);

  return <DeadEnd t={t} locale={locale} here={here} appUrl={appUrl} />;
}
