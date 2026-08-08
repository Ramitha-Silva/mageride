import { redirect } from 'next/navigation';

import { AuthScreen } from '@/components/auth/AuthScreen';
import { landingPath } from '@/server/access';
import { safeNextPath } from '@/server/next-path';
import { getSession } from '@/server/session';

import { first } from './params';

/**
 * **SCR-FP-001 · Sign in** — the screen at `/login`, and where `proxy.ts` sends
 * anybody without a session.
 *
 * The screen itself is {@link AuthScreen}, shared with `/signup`, which is the
 * same card opened on its other tab. What lives here is the route's own two
 * decisions: where an already-signed-in caller goes instead, and which of the
 * query parameters the sign-out and the two federated legs set are honoured.
 */

export const dynamic = 'force-dynamic';

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const next = safeNextPath(first(params.next));

  // Somebody who is already signed in has no business on the sign-in screen; the
  // usual way to arrive here is a bookmark. Sending them on is friendlier than a
  // form that would sign them into the session they already hold.
  const session = await getSession();
  if (session) redirect(next ?? landingPath(session) ?? '/');

  const failed = first(params.error);

  return (
    <AuthScreen
      tab="signIn"
      {...(next ? { next } : {})}
      signedOut={first(params.signedOut) === '1'}
      {...(failed === 'google' || failed === 'apple' ? { failedProvider: failed } : {})}
    />
  );
}
