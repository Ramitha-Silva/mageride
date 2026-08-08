import { redirect } from 'next/navigation';

import { CTA_CLASS_NAMES } from '@mageride/tailwind-preset';

import { Brand } from '@/components/Brand';
import { AppleGlyph, GoogleGlyph } from '@/components/icons';
import { appleSignIn, googleSignIn } from '@/config/env';
import { getTranslator } from '@/i18n/server';
import { landingPath } from '@/server/access';
import { safeNextPath } from '@/server/next-path';
import { getSession } from '@/server/session';

import { SignInForm } from './SignInForm';

/**
 * **Sign-in** — the session half of SCR-FP-001, and the one screen the shell
 * draws.
 *
 * The component's Screens section says it owns no wireframe screen id, and that
 * is right about the *modules*: the organisation form, the vehicle table and the
 * billing screen each belong to a sibling. Sign-in is not a module — there is no
 * session to hand a screen without a form to establish one — so the credential
 * arm and the two federated arms are built here and recorded in the handoff.
 * **SCR-FP-001 itself stays C112's**, which owns the sign-up half of the same
 * card, the organisation setup it leads into, and whatever the platform grows to
 * support of the wireframe's "email verification + password reset · link / unlink
 * identities" line.
 *
 * ## The three things the wireframe asks for that no contract can answer
 *
 * `web_fleet.html` draws a **Create account** button, and iam-svc has no route
 * that registers a portal identity: `POST /v1/fleets` needs the caller to already
 * hold `fleet_owner`, and the only two things that grant it are an existing
 * Owner's `POST /v1/fleets/{id}/members` and a Super Admin's role grant. It also
 * asks for **email verification** and **password reset**, and iam-svc's nine auth
 * operations include neither. And **link / unlink identities**: `iam.federated_identities`
 * is written by a provider sign-in and no route reads, adds or removes a row.
 *
 * So all three are stated in words rather than drawn as controls. A button that
 * posted nowhere would be worse than the sentence that replaced it, and a link to
 * `/signup` would be a dead end that looks like a way out. All three are raised in
 * the C111 handoff, where C112 will find them.
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
  const providers = [
    { id: 'google', config: googleSignIn(), label: t('fleet.signIn.google'), glyph: <GoogleGlyph /> },
    { id: 'apple', config: appleSignIn(), label: t('fleet.signIn.apple'), glyph: <AppleGlyph /> },
  ].filter((provider) => provider.config !== null);

  const signedOut = first(params.signedOut) === '1';
  const failedProvider = first(params.error);

  return (
    <main className="grid min-h-dvh place-items-center bg-surface px-md py-xl">
      {/* `web_fleet.html`: a centred card, 380px. */}
      <div className="w-full max-w-[380px]">
        <div className="mb-lg flex flex-col items-center gap-xs text-center">
          <Brand label={t('fleet.signIn.heading')} size="lg" />
          <p className="text-body-sm text-on-surface-variant">{t('fleet.tagline')}</p>
        </div>

        <div className="flex flex-col gap-md rounded-card border border-outline bg-background p-lg shadow-card">
          {signedOut ? (
            <p className="rounded-md bg-surface-variant px-sm py-xs text-body-sm text-on-surface-variant">
              {t('fleet.signIn.signedOut')}
            </p>
          ) : null}

          {failedProvider === 'google' || failedProvider === 'apple' ? (
            <p role="alert" className="text-body-sm text-error">
              {t('fleet.error.providerFailed', {
                provider: failedProvider === 'google' ? 'Google' : 'Apple',
              })}
            </p>
          ) : null}

          <SignInForm
            labels={{
              email: t('fleet.signIn.email'),
              password: t('fleet.signIn.password'),
              submit: t('fleet.signIn.submit'),
              submitting: t('fleet.signIn.submitting'),
            }}
            {...(next ? { next } : {})}
          />

          {/*
            Absent configuration hides a button rather than rendering one that
            answers `redirect_uri_mismatch`. A control that cannot work is worse
            than no control: the operator has no way to tell the two apart.
          */}
          {providers.length > 0 ? (
            <>
              <div className="flex items-center gap-sm text-caption text-outline-variant">
                <span className="h-px flex-1 bg-outline" />
                {t('fleet.signIn.or')}
                <span className="h-px flex-1 bg-outline" />
              </div>

              <div className="flex gap-sm">
                {providers.map((provider) => (
                  <a
                    key={provider.id}
                    href={
                      next
                        ? `/auth/${provider.id}?next=${encodeURIComponent(next)}`
                        : `/auth/${provider.id}`
                    }
                    className={`${CTA_CLASS_NAMES} flex-1 border border-outline bg-background text-on-surface hover:bg-surface-variant`}
                  >
                    {provider.glyph}
                    {provider.label}
                  </a>
                ))}
              </div>
            </>
          ) : null}

          {/* AL-37 / US-24.5, said out loud: an absence explains nothing by itself. */}
          <p className="rounded-md bg-surface-variant px-sm py-xs text-center text-caption text-on-surface-variant">
            {t('fleet.signIn.noSecondFactor')}
          </p>

          {/*
            The two disclosures. `<details>` because both are closed by default:
            the wireframe's card has no such rows, and this costs it one line each
            rather than two paragraphs.
          */}
          <div className="flex flex-col gap-xxs text-center">
            <Disclosure summary={t('fleet.signIn.forgot')} body={t('fleet.signIn.forgotBody')} />
            <Disclosure
              summary={t('fleet.signIn.newAccount')}
              body={t('fleet.signIn.newAccountBody')}
            />
          </div>
        </div>
      </div>
    </main>
  );
}

function Disclosure({ summary, body }: { summary: string; body: string }) {
  return (
    <details>
      <summary className="cursor-pointer text-caption text-on-surface-variant underline underline-offset-2">
        {summary}
      </summary>
      <p className="pt-xs text-caption text-on-surface-variant">{body}</p>
    </details>
  );
}

function first(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}
