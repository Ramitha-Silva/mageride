import { headers } from 'next/headers';

import { ScreenPlaceholder } from '@/components/ScreenPlaceholder';
import { PATHNAME_HEADER } from '@/server/cookies';

/**
 * The catch-all every screen route falls through to until its own component
 * lands.
 *
 * A sibling's `app/(portal)/vehicles/page.tsx` takes precedence over this
 * automatically — Next prefers the more specific segment — so C112…C116 need
 * register nothing to replace it. What they must do is add their screen to
 * `src/server/routes.ts` if it is a *new* nav entry, because the manifest is what
 * makes a URL reachable at all.
 *
 * **The pathname comes from the header `proxy.ts` stamped**, not from `params`.
 * The catch-all's own params would give the segments, which is the same
 * information rebuilt by hand and one encoding bug away from disagreeing with the
 * URL the proxy actually gated.
 */

export const dynamic = 'force-dynamic';

export default async function CatchAllScreen({
  params,
}: {
  params: Promise<{ screen: string[] }>;
}) {
  const stamped = (await headers()).get(PATHNAME_HEADER);
  const { screen } = await params;

  return <ScreenPlaceholder pathname={stamped ?? `/${screen.join('/')}`} />;
}
