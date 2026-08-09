import type { ReactNode } from 'react';

import type { Locale, WebMessageKey, WebTranslator } from '@/i18n';
import { LOCALE_PARAM, LOCALES } from '@/i18n';

/**
 * The frame all six SCR-WT pages sit in.
 *
 * **This is not app chrome, and the difference is the fence.** C117's first fence
 * is "no app chrome, no login": there is no navigation, no account menu, no tab
 * bar and no way to reach a second ride from this page, because the token that
 * opened it addresses exactly one. What the wireframe draws — and all it draws —
 * is a 46px brand strip with a language control on it, and an optional app-download
 * strip pinned under the content. Those are the two pieces here.
 *
 * The column is capped at 480px and centred. The primary width is 375px (D2 §AP/§FP
 * and C117's fence), and a phone page stretched across a laptop is a phone page
 * nobody can read; the cap is not a breakpoint, so nothing about the layout changes
 * with it.
 */

export interface ShellProps {
  readonly t: WebTranslator;
  readonly locale: Locale;
  /** The top bar's title — one of the six the wireframe gives the six screens. */
  readonly titleKey: WebMessageKey;
  /** The path the language switch returns to, with its own query preserved. */
  readonly here: string;
  /** The app strip's prompt, when the screen carries one. */
  readonly appStrip?: { readonly promptKey: WebMessageKey; readonly href: string | null } | undefined;
  /** Centres the content and stacks it — SCR-WT-001, 005 and 006's `body.center`. */
  readonly centred?: boolean;
  readonly children: ReactNode;
}

export function Shell({ t, locale, titleKey, here, appStrip, centred = false, children }: ShellProps) {
  return (
    <div className="mx-auto flex min-h-dvh w-full max-w-[480px] flex-col bg-background">
      <WebBar t={t} locale={locale} titleKey={titleKey} here={here} />

      <main
        id="main"
        className={
          centred
            ? 'flex flex-1 flex-col items-center justify-center gap-md p-md text-center'
            : 'flex flex-1 flex-col gap-sm p-md'
        }
      >
        {children}
      </main>

      {appStrip ? <AppStrip t={t} promptKey={appStrip.promptKey} href={appStrip.href} /> : null}
    </div>
  );
}

/**
 * The wireframe's 46px orange strip: a square mark, the screen's title, and the
 * language control.
 *
 * `sticky` rather than `fixed` so it never overlaps the content on a short
 * viewport, and `print-hidden` because a printed receipt is a document rather than
 * a screenshot of one.
 */
function WebBar({
  t,
  locale,
  titleKey,
  here,
}: {
  t: WebTranslator;
  locale: Locale;
  titleKey: WebMessageKey;
  here: string;
}) {
  return (
    <header className="print-hidden sticky top-0 z-20 flex h-[46px] shrink-0 items-center gap-xs bg-primary px-sm text-on-primary">
      <span
        aria-hidden="true"
        className="grid size-[26px] shrink-0 place-items-center rounded-sm bg-on-primary text-subtitle font-display font-bold text-primary"
      >
        {t('web.appMark')}
      </span>
      <h1 className="min-w-0 flex-1 truncate text-subtitle font-display font-bold">{t(titleKey)}</h1>
      <LanguageSwitch t={t} locale={locale} here={here} />
    </header>
  );
}

/**
 * The wireframe's "EN ▾".
 *
 * A `<details>` disclosure and three links, which is the whole of it: **no
 * JavaScript, no client component and no state**. The choice is a `?lang=`
 * parameter on the current URL (see `LOCALE_PARAM` — this surface holds no
 * cookie), so switching language is a navigation the server answers, and it works
 * on a phone whose script budget has already been spent on the map.
 *
 * The three names are written in the language they name, which is
 * `@mageride/i18n`'s deliberate rule: somebody who cannot read the current
 * language has to be able to find their own in the list.
 */
function LanguageSwitch({ t, locale, here }: { t: WebTranslator; locale: Locale; here: string }) {
  return (
    <details className="relative shrink-0">
      <summary
        className="flex cursor-pointer list-none items-center gap-xxs rounded-sm px-xs py-xxs text-label font-semibold marker:hidden"
        aria-label={t('web.language.label')}
      >
        {t(`language.${locale}`)}
        <span aria-hidden="true">▾</span>
      </summary>
      <ul className="absolute end-0 top-full z-30 mt-xxs min-w-[9rem] overflow-hidden rounded-sm border border-outline bg-surface py-xxs shadow-elevation-2">
        {LOCALES.map((option) => (
          <li key={option}>
            <a
              href={`${here}${here.includes('?') ? '&' : '?'}${LOCALE_PARAM}=${option}`}
              hrefLang={option}
              aria-current={option === locale ? 'true' : undefined}
              className="block px-sm py-xs text-body-sm text-on-surface aria-[current]:font-semibold aria-[current]:text-primary hover:bg-surface-variant"
            >
              {t(`language.${option}`)}
            </a>
          </li>
        ))}
      </ul>
    </details>
  );
}

/**
 * The wireframe's bottom strip on SCR-WT-002 and SCR-WT-005 — "💡 Want your own
 * deliveries? · Get the app".
 *
 * **With no store URL configured the button is not drawn**, and the prompt goes
 * with it. A control that cannot work is worse than no control, because nobody can
 * tell the two apart; the same rule the Fleet Portal applies to an unconfigured
 * federated sign-in button.
 */
function AppStrip({
  t,
  promptKey,
  href,
}: {
  t: WebTranslator;
  promptKey: WebMessageKey;
  href: string | null;
}) {
  if (!href) return null;

  return (
    <div className="print-hidden flex shrink-0 items-center gap-xs border-t border-outline bg-surface px-md py-sm text-caption text-on-surface-variant">
      <span aria-hidden="true">💡</span>
      <span className="min-w-0 flex-1">{t(promptKey)}</span>
      <a
        href={href}
        rel="noreferrer noopener external"
        className="shrink-0 rounded-sm bg-on-surface px-sm py-xs text-caption font-semibold text-background"
      >
        {t('web.app.get')}
      </a>
    </div>
  );
}
