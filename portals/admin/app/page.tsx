import { redirect } from 'next/navigation';

import { getTranslator } from '@/i18n/server';
import { landingPath } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * `/` — not a page, a routing decision.
 *
 * It sends the operator to their **first permitted screen** rather than to
 * `/dashboard`, because URD §2.3 gives the Verification Officer ➖ on "Analytics &
 * reporting" and D2 §AP says as much in words: "Verification Officer → onboarding
 * queue only". A fixed landing route would greet the one role the queues were
 * designed around with a 403 on their first page.
 *
 * The only case that renders anything is the one with nowhere to go.
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
        <h1 className="text-headline font-display">{t('admin.noModules.title')}</h1>
        <p className="pt-xs text-body-sm text-on-surface-variant">{t('admin.noModules.body')}</p>
      </div>
    </main>
  );
}
