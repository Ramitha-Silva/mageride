import { forbidden, notFound, redirect } from 'next/navigation';

import { getTranslator } from '@/i18n/server';
import { refusalFor } from '@/server/access';
import { resolveScreenRoute } from '@/server/routes';
import { getSession } from '@/server/session';

/**
 * Every screen the shell can reach but no component has built yet.
 *
 * The Fleet Portal is twelve screens across six build components; C111 built the
 * shell they live in. Until a sibling lands its own `app/(portal)/…/page.tsx` —
 * which takes precedence over the catch-all automatically — a permitted route
 * renders this: the screen's own name, its wireframe id, the service that answers
 * its API, and an honest statement that the screen is not built. That is what
 * makes the shell demonstrably navigable now and a no-op to replace later.
 *
 * The three refusals below are a **second** gate rather than the gate: `proxy.ts`
 * has already answered a request the caller may not make. What survives here is
 * the distinction as the shell understands it, for the case where this renders
 * without the proxy in front of it — a direct render in a test, or a future
 * deployment whose proxy matcher stops covering a path.
 */
export async function ScreenPlaceholder({ pathname }: { pathname: string }) {
  // No Fleet Portal screen claims this URL. Deliberately a 404 and not a 403: it
  // is not a refusal, there is nothing there for anybody.
  const screen = resolveScreenRoute(pathname);
  if (!screen) notFound();

  const session = await getSession();
  if (!session) forbidden();

  const refusal = refusalFor(session, screen);
  // The verification gate is not a refusal of this person, and `/pending` is the
  // page that says what is actually happening.
  if (refusal === 'pending') redirect('/pending');
  if (refusal) forbidden();

  const t = await getTranslator();

  return (
    <section className="mx-auto flex w-full max-w-[720px] flex-col gap-sm rounded-card border border-outline bg-background p-lg shadow-card">
      <h2 className="text-title font-display">{t('fleet.screen.pendingTitle')}</h2>
      <p className="text-body-sm text-on-surface-variant">{t('fleet.screen.pendingBody')}</p>
      <p className="text-caption text-outline-variant">
        {t('fleet.screen.wireframe', { screen: screen.screen })} ·{' '}
        {t('fleet.screen.servedBy', { service: screen.ownedBy })}
      </p>
    </section>
  );
}
