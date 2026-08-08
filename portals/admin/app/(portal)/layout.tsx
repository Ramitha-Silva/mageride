import { redirect } from 'next/navigation';

import type { Role } from '@/api/types';
import { PortalChrome } from '@/components/PortalChrome';
import { buildNavModel } from '@/components/nav-model';
import { LOCALES } from '@/i18n';
import { getAppearance, getLocale, getTranslator } from '@/i18n/server';
import { displayIdentity, getSession } from '@/server/session';

/**
 * Everything behind sign-in renders inside this.
 *
 * **The RBAC gate is not here**, and the reason is structural: an App Router
 * layout is reused, not re-rendered, when navigation moves between its children,
 * so a guard here would run on the first page of a session and never again.
 * `proxy.ts` holds it instead, on every request including the RSC fetch a
 * client-side navigation makes. What this layout does is draw the console the
 * same evaluation produced, so the nav and the gate cannot disagree.
 *
 * The session is fetched once per full page load rather than per navigation, and
 * that is the right trade: a permission change reaches the *nav* on the next full
 * load, and the *routes* within `ADMIN_PORTAL_SESSION_CACHE_SECONDS`, because the
 * proxy re-evaluates. Neither is the enforcement point — every endpoint decides
 * for itself (AL-06, US-21.1).
 */
export default async function PortalLayout({ children }: { children: React.ReactNode }) {
  const session = await getSession();
  // The proxy has already redirected an unauthenticated caller. Repeating it here
  // is what makes that a belt rather than the only strap: a layout that assumed a
  // session and got null would render the chrome of a signed-out console.
  if (!session) redirect('/login');

  const [t, locale, appearance, identity] = await Promise.all([
    getTranslator(),
    getLocale(),
    getAppearance(),
    displayIdentity(),
  ]);

  // A role that opens nothing is told so. `GET /v1/admin/session` is gated on
  // being authenticated rather than on a feature area precisely so this case can
  // be answered — see `SessionEndpoints`: "a blank console with no explanation
  // rather than a refusal".
  if (session.menu.length === 0) {
    return (
      <main className="grid min-h-dvh place-items-center bg-surface p-md">
        <div className="max-w-[520px] rounded-card border border-outline bg-background p-lg text-center shadow-card">
          <h1 className="text-headline font-display">{t('admin.noModules.title')}</h1>
          <p className="pt-xs text-body-sm text-on-surface-variant">{t('admin.noModules.body')}</p>
        </div>
      </main>
    );
  }

  const accountName = identity?.name ?? identity?.email ?? t('admin.user.menu');

  return (
    <PortalChrome
      groups={buildNavModel(session.menu, t)}
      accountName={accountName}
      roles={session.roles.map((role) => t(roleKey(role)))}
      appearances={(['light', 'dark', 'system'] as const).map((value) => ({
        value,
        label: t(`admin.appearance.${value}` as const),
        current: value === appearance,
      }))}
      locales={LOCALES.map((value) => ({
        value,
        label: t(`language.${value}` as const),
        current: value === locale,
      }))}
      labels={{
        appName: t('admin.appName'),
        nav: t('admin.nav.label'),
        openNav: t('admin.nav.open'),
        closeNav: t('admin.nav.close'),
        skipToContent: t('admin.skipToContent'),
        account: {
          menu: t('admin.user.menu'),
          roles: t('admin.user.roles'),
          signOut: t('admin.user.signOut'),
          appearance: t('admin.appearance.label'),
          language: t('admin.language.label'),
        },
      }}
    >
      {children}
    </PortalChrome>
  );
}

/** The nine canonical roles are `snake_case` on the wire and keyed the same way. */
function roleKey(role: Role) {
  return `admin.role.${role}` as const;
}
