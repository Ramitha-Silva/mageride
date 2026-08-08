import { redirect } from 'next/navigation';

import type { StatusTone } from '@mageride/ui';

import type { FleetRole, FleetStatus } from '@/api/types';
import { OrgStatusBanner } from '@/components/OrgStatusBanner';
import { PortalChrome, type StatusChip } from '@/components/PortalChrome';
import { buildNavModel } from '@/components/nav-model';
import { LOCALES } from '@/i18n';
import { getAppearance, getLocale, getTranslator } from '@/i18n/server';
import { permittedScreens } from '@/server/access';
import { displayIdentity, getSession } from '@/server/session';

/**
 * Everything behind sign-in renders inside this.
 *
 * **The gate is not here**, and the reason is structural: an App Router layout is
 * reused, not re-rendered, when navigation moves between its children, so a guard
 * here would run on the first page of a session and never again. `proxy.ts` holds
 * it instead, on every request including the RSC fetch a client-side navigation
 * makes. What this layout does is draw the console the same evaluation produced,
 * so the nav and the gate cannot disagree.
 *
 * The session is fetched once per full page load rather than per navigation, and
 * that is the right trade: a sub-role change or a verification approval reaches
 * the *nav* on the next full load, and the *routes* within
 * `FLEET_PORTAL_SESSION_CACHE_SECONDS`, because the proxy re-evaluates. Neither is
 * the enforcement point — `FleetAccessFilter` re-reads `iam.fleet_members` on
 * every request (AL-06, US-21.1).
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

  // An account that opens nothing is told so. It is a real state — a member whose
  // `fleet_owner` grant was revoked keeps their password — and a blank console
  // with no explanation is the one answer that helps nobody.
  if (permittedScreens(session).length === 0) {
    return (
      <main className="grid min-h-dvh place-items-center bg-surface p-md">
        <div className="max-w-[520px] rounded-card border border-outline bg-background p-lg text-center shadow-card">
          <h1 className="text-headline font-display">{t('fleet.noScreens.title')}</h1>
          <p className="pt-xs text-body-sm text-on-surface-variant">{t('fleet.noScreens.body')}</p>
        </div>
      </main>
    );
  }

  const accountName = identity?.name ?? identity?.email ?? t('fleet.user.menu');

  return (
    <PortalChrome
      groups={buildNavModel(session, t)}
      accountName={accountName}
      organisation={session.organisation?.name ?? null}
      role={session.fleetRole ? t(roleKey(session.fleetRole)) : null}
      status={statusChip(session.organisation?.status, t)}
      appearances={(['light', 'dark', 'system'] as const).map((value) => ({
        value,
        label: t(`fleet.appearance.${value}` as const),
        current: value === appearance,
      }))}
      locales={LOCALES.map((value) => ({
        value,
        label: t(`language.${value}` as const),
        current: value === locale,
      }))}
      labels={{
        appName: t('fleet.appName'),
        nav: t('fleet.nav.label'),
        openNav: t('fleet.nav.open'),
        closeNav: t('fleet.nav.close'),
        skipToContent: t('fleet.skipToContent'),
        account: {
          menu: t('fleet.user.menu'),
          role: t('fleet.user.role'),
          signOut: t('fleet.user.signOut'),
          appearance: t('fleet.appearance.label'),
          language: t('fleet.language.label'),
        },
      }}
    >
      <OrgStatusBanner session={session} />
      {children}
    </PortalChrome>
  );
}

/** The three org-scoped sub-roles are lower-case on the wire and keyed the same way. */
function roleKey(fleetRole: FleetRole) {
  return `fleet.role.${fleetRole}` as const;
}

/**
 * The topbar chip, or nothing at all.
 *
 * An account with no organisation gets no chip rather than a "Pending" one: there
 * is nothing in verification, and a chip reporting the state of something that
 * does not exist is worse than the absence of one.
 */
function statusChip(
  status: FleetStatus | undefined,
  t: (key: 'fleet.status.pending' | 'fleet.status.approved' | 'fleet.status.rejected') => string,
): StatusChip | null {
  const TONES: Readonly<Record<FleetStatus, StatusTone>> = {
    PENDING: 'warning',
    APPROVED: 'success',
    REJECTED: 'error',
  };

  switch (status) {
    case 'PENDING':
      return { label: t('fleet.status.pending'), tone: TONES.PENDING };
    case 'APPROVED':
      return { label: t('fleet.status.approved'), tone: TONES.APPROVED };
    case 'REJECTED':
      return { label: t('fleet.status.rejected'), tone: TONES.REJECTED };
    default:
      return null;
  }
}
