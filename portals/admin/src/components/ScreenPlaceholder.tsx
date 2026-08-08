import { forbidden, notFound } from 'next/navigation';

import { getTranslator } from '@/i18n/server';
import { resolveScreen } from '@/server/access';
import { resolveRoute } from '@/server/routes';
import { getSession } from '@/server/session';

/**
 * Every screen the shell can reach but no component has built yet.
 *
 * The Admin Portal is nineteen screens across seven build components; C104 built
 * the shell they live in. Until a sibling lands its own `app/(portal)/…/page.tsx`
 * — which takes precedence over the catch-all automatically — a permitted route
 * renders this: the screen's own name, the service that answers its API, and an
 * honest statement that the screen is not built. That is what makes the shell
 * demonstrably navigable now and a no-op to replace later.
 *
 * Both refusals below are a **second** gate rather than the gate: `proxy.ts` has
 * already answered a request the caller may not make, and it answers a URL no
 * screen claims the same way — 403, not 404, because deny-by-default cannot make
 * an exception for "we could not find a screen for this" without that becoming
 * the way a future unregistered route gets in ungated. What survives here is the
 * distinction as the shell understands it, for the case where this renders
 * without the proxy in front of it.
 *
 * **Δ C106: it is a component rather than the catch-all's body.** `/verification`
 * gained a `[subjectId]` segment, and a single dynamic segment out-ranks a
 * catch-all — so `/verification/expiring`, which is the *document-expiry* screen
 * and a different nav item, now resolves to the verification detail page's file.
 * That page hands any path belonging to another screen straight back here, so the
 * screen C110 has yet to build still says so rather than rendering as a
 * verification subject named "expiring".
 */
export async function ScreenPlaceholder({ pathname }: { pathname: string }) {
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
