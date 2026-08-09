import { trackScreen } from '@/server/screen';
import type { SearchParams } from '@/server/render';

/**
 * **SCR-WT-001 — the landing / token gate at `/track?token=…`.**
 *
 * This is the URL notification-svc puts in the SMS (D1' Δ 2026-07-05:
 * `passenger.mageride.lk/track?token=…`), and it is the only entry point to the
 * whole surface. What happens next is `trackScreen`, which is D2's own sentence:
 * validate, then route by scope, with an expired or unknown token reaching
 * SCR-WT-006 and an already-delivered parcel reaching SCR-WT-005.
 *
 * **The token is redeemed on the server before anything is rendered.** That is the
 * C117 fence "render nothing before it validates", and it is held by where the
 * `await` is rather than by a check anybody has to remember: a dead token never
 * produces a tree that holds a ride, because `<DeadEnd>` takes no snapshot.
 * public-bff holds the other half — the 404/410 is produced *before any ride row is
 * read*, so the payload this page never renders was never fetched.
 *
 * `landing` is set here and nowhere else: a package recipient did not order this
 * parcel, so the SMS's own URL explains it before showing a map.
 */
export const dynamic = 'force-dynamic';

export default async function TokenGatePage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  const params = await searchParams;
  const raw = params['token'];

  return trackScreen({
    token: Array.isArray(raw) ? raw[0] : raw,
    pathname: '/track',
    searchParams: params,
    landing: true,
  });
}
