import { forbidden, notFound } from 'next/navigation';

import { getTranslator } from '@/i18n/server';
import { resolveScreen } from '@/server/access';
import { resolveRoute } from '@/server/routes';
import { getSession } from '@/server/session';

/**
 * Every screen the shell can reach but does not own.
 *
 * The Admin Portal is nineteen screens across seven build components; C104 builds
 * the shell they live in. Until a sibling lands its own `app/(portal)/…/page.tsx`
 * — which takes precedence over this catch-all automatically — a permitted route
 * renders this: the screen's own name, the service that answers its API, and an
 * honest statement that the screen is not built. That is what makes the shell
 * demonstrably navigable now and a no-op to replace later.
 *
 * Both refusals below are a **second** gate rather than the gate: `proxy.ts` has
 * already answered a request the caller may not make, and it answers a URL no
 * screen claims the same way — 403, not 404, because deny-by-default cannot make
 * an exception for "we could not find a screen for this" without that becoming
 * the way a future unregistered route gets in ungated. What survives here is the
 * distinction as the shell understands it, for the case where this file renders
 * without the proxy in front of it, and because this page is the template every
 * sibling component's screen starts from.
 */

export const dynamic = 'force-dynamic';

export default async function ScreenPlaceholder({
  params,
}: {
  params: Promise<{ screen: string[] }>;
}) {
  const { screen } = await params;
  const pathname = `/${screen.join('/')}`;

  // No Admin Portal screen claims this URL. Deliberately a 404 and not a 403: it
  // is not a refusal, there is nothing there for anybody.
  if (!resolveRoute(pathname)) notFound();

  const session = await getSession();
  const resolved = session ? resolveScreen(session.menu, pathname) : null;
  if (!resolved) forbidden();

  const t = await getTranslator();

  return (
    <section className="mx-auto flex max-w-[720px] flex-col gap-sm rounded-card border border-outline bg-background p-lg shadow-card">
      <h2 className="text-title font-display">{t('admin.screen.pendingTitle')}</h2>
      <p className="text-body-sm text-on-surface-variant">{t('admin.screen.pendingBody')}</p>
      <p className="text-caption text-outline-variant">
        {t('admin.screen.servedBy', { service: resolved.item.ownedBy })}
      </p>
    </section>
  );
}
