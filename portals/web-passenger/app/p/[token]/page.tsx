import { trackScreen } from '@/server/screen';
import type { SearchParams } from '@/server/render';

/**
 * **`/p/{token}` — the proxy pair's URL shape.**
 *
 * The wireframe gives SCR-WT-003 and SCR-WT-004 the same shape (`/p/9f2…`) because
 * they are the same link at two moments of the same booking: first "where are
 * you?", then "here is your car". Which one renders is the **token's scope**, read
 * off `safety.trip_share_tokens` by public-bff and dispatched on in `trackScreen` —
 * the client cannot ask for the other, because there is no parameter that selects a
 * variant and the two tokens are different rows.
 */
export const dynamic = 'force-dynamic';

export default async function ProxyPage({
  params,
  searchParams,
}: {
  params: Promise<{ token: string }>;
  searchParams: Promise<SearchParams>;
}) {
  const { token } = await params;

  return trackScreen({
    token,
    pathname: `/p/${token}`,
    searchParams: await searchParams,
  });
}
