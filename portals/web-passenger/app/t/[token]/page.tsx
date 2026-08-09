import { trackScreen } from '@/server/screen';
import type { SearchParams } from '@/server/render';

/**
 * **`/t/{token}` — the parcel's URL shape**, and the screen SCR-WT-001's own button
 * goes to.
 *
 * The wireframe gives the recipient's link its own shape (`/t/aХ8…`) and gives the
 * proxy pair another (`/p/9f2…`), so the address bar says which kind of link
 * somebody is holding. Which screen renders is still the **token's**, not the
 * path's: `trackScreen` dispatches on the scope public-bff read off
 * `safety.trip_share_tokens`, so a proxy link pasted here renders that rider's own
 * screen rather than a refusal.
 *
 * For a parcel this is SCR-WT-002 until it arrives and SCR-WT-005 afterwards —
 * D2's "Delivered → auto-advance to 005", as a *server* decision. The live feed's
 * `resolved` frame calls `router.refresh()` and this function runs again, so the
 * advance re-reads the token: a link safety-svc revoked at trip end becomes
 * SCR-WT-006 instead of a receipt.
 */
export const dynamic = 'force-dynamic';

export default async function PackagePage({
  params,
  searchParams,
}: {
  params: Promise<{ token: string }>;
  searchParams: Promise<SearchParams>;
}) {
  const { token } = await params;

  return trackScreen({
    token,
    pathname: `/t/${token}`,
    searchParams: await searchParams,
  });
}
