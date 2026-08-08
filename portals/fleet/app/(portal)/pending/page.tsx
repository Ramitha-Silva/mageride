import Link from 'next/link';
import { redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { getTranslator } from '@/i18n/server';
import { landingPath, permittedScreens } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **The pending-verification state** (US-13.A7), and where `proxy.ts` rewrites a
 * request for a screen an unapproved organisation cannot open.
 *
 * ## Why this renders rather than refusing
 *
 * `/denied` calls `forbidden()` and answers a real 403, because that refusal is
 * about the caller and there is nothing on the page but the fact of it. This one
 * is not about the caller at all: everybody in the organisation is waiting on the
 * same verification officer, the wait ends by itself, and the useful thing to put
 * on the screen is what can be done meanwhile. So it renders inside the chrome,
 * with the nav and the status chip still visible, and it links to the setup
 * screens that *are* open.
 *
 * The enforcement is not weakened by that. Every route behind the blocked screens
 * sits in a group carrying `RequireApprovedFleet()`, so fleet-svc answers
 * `403 fleet-not-approved` to any request that reaches it — including one made by
 * a caller who typed the URL. What this page stops is the console pretending
 * those screens are there.
 *
 * A **rejected** organisation lands here too, with the officer's reason, because
 * the state of the gate is the same and the next action is not.
 */

export const dynamic = 'force-dynamic';

export default async function PendingPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const t = await getTranslator();
  const status = session.organisation?.status;

  // An approved organisation has no reason to be here; the usual way to arrive is
  // a bookmark taken while the verification was still outstanding.
  if (status === 'APPROVED' || !session.organisation) {
    redirect(landingPath(session) ?? '/');
  }

  const rejected = status === 'REJECTED';
  const openScreens = permittedScreens(session);

  return (
    <section className="mx-auto flex w-full max-w-[720px] flex-col gap-md rounded-card border border-outline bg-background p-lg shadow-card">
      <div className="flex flex-wrap items-center gap-sm">
        <h2 className="flex-1 text-title font-display">
          {rejected ? t('fleet.rejected.title') : t('fleet.pending.title')}
        </h2>
        <StatusPill tone={rejected ? 'error' : 'warning'}>
          {rejected ? t('fleet.status.rejected') : t('fleet.status.pending')}
        </StatusPill>
      </div>

      <p className="text-body-sm text-on-surface-variant">
        {rejected ? t('fleet.rejected.body') : t('fleet.pending.body')}
      </p>

      {rejected && session.organisation.rejectionReason ? (
        <p className="rounded-md border border-error/40 bg-error/10 px-sm py-xs text-body-sm">
          {t('fleet.rejected.reason', { reason: session.organisation.rejectionReason })}
        </p>
      ) : null}

      <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
        {t('fleet.pending.blocked')}
      </p>

      {!rejected ? (
        <p className="text-body-sm text-on-surface-variant">{t('fleet.pending.next')}</p>
      ) : null}

      {/*
        The screens that *are* open, drawn from the same evaluation the nav is —
        so this list cannot offer something the sidebar does not, and a Viewer
        sees the two screens they can actually read rather than a list of things
        to do that are not theirs.
      */}
      {openScreens.length > 0 ? (
        <ul className="flex flex-wrap gap-xs">
          {openScreens.map((screen) => (
            <li key={screen.key}>
              <Link
                href={screen.path}
                className="inline-flex rounded-sm border border-outline px-sm py-xs text-body-sm text-on-surface hover:bg-surface-variant"
              >
                {t(screen.labelKey)}
              </Link>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
