import { redirect } from 'next/navigation';

import { getTranslator } from '@/i18n/server';
import { landingPath } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * `/` — not a page, a routing decision.
 *
 * It sends the member to their **landing screen**, which is the dashboard once
 * there is an approved organisation and the organisation's own setup screen
 * before then (`landingPath`). A fixed `/dashboard` would greet a fleet owner on
 * their first morning with counts of nothing, above a nav whose operating half is
 * not there yet.
 *
 * The only case that renders anything is the one with nowhere to go: an account
 * that signed in and holds no fleet role at all.
 */

export const dynamic = 'force-dynamic';

export default async function RootPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const destination = landingPath(session);
  if (destination) redirect(destination);

  const t = await getTranslator();

  return (
    <main className="grid min-h-dvh place-items-center bg-surface p-md">
      <div className="max-w-[520px] rounded-card border border-outline bg-background p-lg text-center shadow-card">
        <h1 className="text-headline font-display">{t('fleet.noScreens.title')}</h1>
        <p className="pt-xs text-body-sm text-on-surface-variant">{t('fleet.noScreens.body')}</p>
      </div>
    </main>
  );
}
