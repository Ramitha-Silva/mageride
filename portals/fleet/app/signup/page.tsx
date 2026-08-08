import { redirect } from 'next/navigation';

import { AuthScreen } from '@/components/auth/AuthScreen';
import { landingPath } from '@/server/access';
import { safeNextPath } from '@/server/next-path';
import { getSession } from '@/server/session';

import { first } from '../login/params';

/**
 * **SCR-FP-001 · Create account** — `web_fleet.html`'s own address bar for this
 * screen (`fleet.mageride.lk/signup`).
 *
 * It is the sign-in card opened on its other tab, not a second screen: the
 * wireframe draws one card whose states are "sign-up vs login", and two routes
 * onto one component is what makes that URL real without duplicating anything.
 *
 * **Nothing here posts.** There is no self-service registration on this platform
 * — see {@link AuthScreen} for the three affordances the wireframe draws that no
 * contract answers — so the tab explains the two ways a Fleet Portal account
 * actually comes to exist. A route that 404'd beside a screen the wireframe
 * draws would be the worse of the two answers: it tells an operator who typed
 * the address that MageRide has no such thing, when what it has is a different
 * path to it.
 */

export const dynamic = 'force-dynamic';

export default async function SignUpPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const next = safeNextPath(first(params.next));

  const session = await getSession();
  if (session) redirect(next ?? landingPath(session) ?? '/');

  return <AuthScreen tab="signUp" {...(next ? { next } : {})} />;
}
