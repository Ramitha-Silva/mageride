import { redirect } from 'next/navigation';

import { CTA_CLASS_NAMES } from '@mageride/tailwind-preset';

import { Brand } from '@/components/Brand';
import { GoogleGlyph } from '@/components/icons';
import { googleSignIn } from '@/config/env';
import { getTranslator } from '@/i18n/server';
import { landingPath } from '@/server/access';
import { safeNextPath } from '@/server/next-path';
import { getSession } from '@/server/session';

import { SignInForm } from './SignInForm';

/**
 * **SCR-AP-001 · `admin_login`** — the one screen the shell owns.
 *
 * The component's Screens section says it owns no wireframe screen ID, and that
 * is right about the *modules*: the queues, the directories and the reports each
 * belong to a sibling. Sign-in is not a module. It is the deliverable's "session
 * handling for password / Google sign-in against iam-svc, failed-attempt lockout
 * surfaced", and there is no session to hand a module without a form to establish
 * one — so it is built here and recorded in the handoff.
 *
 * D2 SCR-AP-001: centred card, max-width 400px, email + password, Google
 * Sign-In, **no MFA/TOTP challenge block**.
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

  const t = await getTranslator();
  const google = googleSignIn();
  const signedOut = first(params.signedOut) === '1';
  const googleFailed = first(params.error) === 'google';

  return (
    <main className="grid min-h-dvh place-items-center bg-surface px-md py-xl">
      {/* D2 SCR-AP-001: "Centered card (max-width 400px)". */}
      <div className="w-full max-w-[400px]">
        <div className="mb-lg flex flex-col items-center gap-xs text-center">
          <Brand label={t('admin.signIn.heading')} size="lg" />
          <p className="text-body-sm text-on-surface-variant">{t('admin.tagline')}</p>
        </div>

        <div className="flex flex-col gap-md rounded-card border border-outline bg-background p-lg shadow-card">
          {signedOut ? (
            <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
              {t('admin.signIn.signedOut')}
            </p>
          ) : null}

          {googleFailed ? (
            <p role="alert" className="text-body-sm text-error">
              {t('admin.error.googleFailed')}
            </p>
          ) : null}

          <SignInForm
            labels={{
              email: t('admin.signIn.email'),
              password: t('admin.signIn.password'),
              submit: t('admin.signIn.submit'),
              submitting: t('admin.signIn.submitting'),
            }}
            {...(next ? { next } : {})}
          />

          {/*
            Absent configuration hides the button rather than rendering one that
            answers `redirect_uri_mismatch`. A control that cannot work is worse
            than no control: the operator has no way to tell the two apart.
          */}
          {google ? (
            <>
              <div className="flex items-center gap-sm text-caption text-outline-variant">
                <span className="h-px flex-1 bg-outline" />
                {t('admin.signIn.or')}
                <span className="h-px flex-1 bg-outline" />
              </div>

              <a
                href={next ? `/auth/google?next=${encodeURIComponent(next)}` : '/auth/google'}
                className={`${CTA_CLASS_NAMES} w-full border border-outline bg-background text-on-surface hover:bg-surface-variant`}
              >
                <GoogleGlyph />
                {t('admin.signIn.google')}
              </a>
            </>
          ) : null}

          {/*
            AL-37 / US-24.5, said out loud. The MFA step was removed from a screen
            staff had been told to expect it on, and an absence explains nothing
            by itself.
          */}
          <p className="rounded-md bg-surface-variant px-sm py-xs text-center text-caption text-on-surface-variant">
            {t('admin.signIn.noSecondFactor')}
          </p>

          {/*
            The deliverable's forgot-password affordance, and it is a disclosure
            rather than a link because **no reset route exists on any contract**.
            `iam.yaml` has nine auth operations and none of them resets a password;
            D2 gives one to SCR-FP-001 (the Fleet Portal) and none to SCR-AP-001;
            AL-06 says internal roles "are provisioned only by Super Admin". So the
            honest control is the sentence telling an operator what actually
            unblocks them. A link to `/forgot-password` would be a dead end that
            looks like a way out, and a form that posted somewhere would be this
            portal inventing a credential path — the one thing `session.ts` says it
            will never do. Recorded as a spec gap in the C105 handoff.

            `<details>` because it is closed by default: the wireframe's card has
            no such row, and this costs it one line rather than a block.
          */}
          <details className="text-center">
            <summary className="cursor-pointer text-caption text-on-surface-variant underline underline-offset-2">
              {t('admin.signIn.forgot')}
            </summary>
            <p className="pt-xs text-caption text-on-surface-variant">
              {t('admin.signIn.forgotBody')}
            </p>
          </details>
        </div>
      </div>
    </main>
  );
}

function first(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
