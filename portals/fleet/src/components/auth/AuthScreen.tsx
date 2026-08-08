import { CTA_CLASS_NAMES } from '@mageride/tailwind-preset';
import { Tabs } from '@mageride/ui';

import { SignInForm } from '@/components/auth/SignInForm';
import { Brand } from '@/components/Brand';
import { AppleGlyph, GoogleGlyph } from '@/components/icons';
import { appleSignIn, googleSignIn } from '@/config/env';
import { getTranslator } from '@/i18n/server';

/**
 * **SCR-FP-001 · `fleet_login_signup`** — the whole screen, at both of the
 * wireframe's addresses.
 *
 * `web_fleet.html` draws one centred 380px card whose states line reads
 * "sign-up vs login; unverified email banner; password reset; identity
 * link/unlink", with the address bar on `/signup`. So the card has two tabs and
 * two routes: `/login` opens on Sign in, `/signup` opens on Create account, and
 * they are the same screen.
 *
 * ## Three of the wireframe's affordances have no route on any contract
 *
 * This is the wall C111 hit and recorded, and C112 hits it again from the other
 * side. In each case the tab **states the real path in words** rather than
 * drawing a control that posts nowhere:
 *
 *  1. **Sign-up.** `POST /v1/fleets` registers an *organisation* and is gated on
 *     already holding `fleet_owner`. The only two things that grant it are an
 *     existing Owner's `POST /v1/fleets/{id}/members` and a Super Admin's role
 *     grant, so a new operator cannot create an account here or anywhere.
 *  2. **Email verification and password reset.** iam-svc has nine auth
 *     operations and none of them verifies an address or resets a password.
 *  3. **Identity link/unlink.** `iam.federated_identities` is written by a
 *     provider sign-in and no route reads, adds or removes a row.
 *
 * A "Create account" button that failed would be worse than the sentence that
 * replaced it, because an operator cannot tell a broken control from an absent
 * feature. All three are raised again in the C112 handoff, with the routes that
 * would close them.
 */

export type AuthTab = 'signIn' | 'signUp';

export async function AuthScreen({
  tab,
  next,
  signedOut = false,
  failedProvider,
}: {
  tab: AuthTab;
  /** Where to land after signing in — already validated by `safeNextPath`. */
  next?: string;
  signedOut?: boolean;
  failedProvider?: 'google' | 'apple';
}) {
  const t = await getTranslator();

  const providers = [
    {
      id: 'google',
      config: googleSignIn(),
      label: t('fleet.signIn.google'),
      glyph: <GoogleGlyph />,
    },
    { id: 'apple', config: appleSignIn(), label: t('fleet.signIn.apple'), glyph: <AppleGlyph /> },
  ].filter((provider) => provider.config !== null);

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

          {failedProvider ? (
            <p role="alert" className="text-body-sm text-error">
              {t('fleet.error.providerFailed', {
                provider: failedProvider === 'google' ? 'Google' : 'Apple',
              })}
            </p>
          ) : null}

          <Tabs
            label={t('fleet.auth.tabs')}
            defaultValue={tab}
            items={[
              {
                value: 'signIn',
                label: t('fleet.auth.tab.signIn'),
                content: (
                  <div className="flex flex-col gap-md">
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
                      Absent configuration hides a button rather than rendering
                      one that answers `redirect_uri_mismatch`. A control that
                      cannot work is worse than no control: the operator has no
                      way to tell the two apart.
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

                    <Disclosure
                      summary={t('fleet.signIn.forgot')}
                      body={t('fleet.signIn.forgotBody')}
                    />
                  </div>
                ),
              },
              {
                value: 'signUp',
                label: t('fleet.auth.tab.signUp'),
                content: (
                  <div className="flex flex-col gap-sm">
                    <h2 className="text-subtitle font-semibold">{t('fleet.signUp.title')}</h2>

                    <p className="rounded-md border border-warning/40 bg-warning/10 px-sm py-xs text-body-sm text-on-surface">
                      {t('fleet.signUp.unavailable')}
                    </p>

                    <Route index={1} body={t('fleet.signUp.byOwner')} />
                    <Route index={2} body={t('fleet.signUp.byMageRide')} />

                    <p className="text-caption text-on-surface-variant">
                      {t('fleet.signUp.thenOrg')}
                    </p>

                    <Disclosure
                      summary={t('fleet.signUp.verification')}
                      body={t('fleet.signUp.verificationBody')}
                    />
                    <Disclosure
                      summary={t('fleet.signUp.identities')}
                      body={t('fleet.signUp.identitiesBody')}
                    />
                  </div>
                ),
              },
            ]}
          />
        </div>
      </div>
    </main>
  );
}

/** One of the two ways a Fleet Portal account actually comes to exist. */
function Route({ index, body }: { index: number; body: string }) {
  return (
    <div className="flex gap-sm rounded-md border border-outline px-sm py-xs">
      <span
        aria-hidden="true"
        className="mt-px grid size-5 shrink-0 place-items-center rounded-full bg-secondary-container text-caption font-semibold text-secondary"
      >
        {index}
      </span>
      <p className="text-body-sm text-on-surface-variant">{body}</p>
    </div>
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
