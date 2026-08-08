import type { FleetSession } from '@/api/types';
import { getTranslator } from '@/i18n/server';
import { holdsGrant } from '@/server/access';

/**
 * The two facts that change what every screen below can do, said once at the top
 * of the console rather than beside each control they affect.
 *
 *  - **The organisation is not approved yet** (US-13.A7). Vehicle onboarding and
 *    driver assignment are not in the nav at all, and a page that simply lacked
 *    them would read as a page that had lost them.
 *  - **This session is read-only.** A Viewer's rows carry no `write` in any area,
 *    so every mutating control everywhere is absent — which is correct and, with
 *    no explanation, indistinguishable from a broken build.
 *
 * Both are derived from the session rather than passed in, so a screen cannot
 * forget to render them and cannot render a stale one.
 *
 * The read-only notice is deliberately **not** shown while the pending notice is:
 * two banners saying two different things about why a button is missing is worse
 * than the more urgent one on its own, and an unapproved organisation's Viewer
 * has the same next step as everybody else in it.
 */
export async function OrgStatusBanner({ session }: { session: FleetSession }) {
  const t = await getTranslator();
  const status = session.organisation?.status;

  if (status === 'REJECTED') {
    return (
      <Banner tone="error">
        {t('fleet.banner.rejected')}
        {session.organisation?.rejectionReason ? (
          <span className="block pt-xxs">
            {t('fleet.rejected.reason', { reason: session.organisation.rejectionReason })}
          </span>
        ) : null}
      </Banner>
    );
  }

  if (status === 'PENDING') {
    return <Banner tone="warning">{t('fleet.banner.pending')}</Banner>;
  }

  // "No write anywhere" rather than "the sub-role is viewer": the question the
  // banner answers is whether this session can change anything, and the honest
  // source for that is the evaluation, not the seat's name. An organisation that
  // ever gains a fourth sub-role gets the right banner without a change here.
  const readOnly =
    session.fleetId !== null &&
    !holdsGrant(session, 'fleet-operations', 'write') &&
    !holdsGrant(session, 'fleet-billing', 'write');

  if (readOnly) {
    return <Banner tone="info">{t('fleet.banner.viewer')}</Banner>;
  }

  return null;
}

const TONES = {
  info: 'border-secondary/30 bg-secondary/10 text-on-surface',
  warning: 'border-warning/40 bg-warning/10 text-on-surface',
  error: 'border-error/40 bg-error/10 text-on-surface',
} as const;

function Banner({ tone, children }: { tone: keyof typeof TONES; children: React.ReactNode }) {
  return (
    <div
      // `status` rather than `alert`: it is a standing condition of the console,
      // not something that has just happened, and an assertive live region would
      // interrupt a screen reader on every navigation.
      role="status"
      className={`rounded-md border px-sm py-xs text-body-sm ${TONES[tone]}`}
    >
      {children}
    </div>
  );
}
