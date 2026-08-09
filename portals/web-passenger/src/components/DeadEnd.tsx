import { CTA_CLASS_NAMES } from '@mageride/ui';

import type { Locale, WebTranslator } from '@/i18n';

import { Shell } from './Shell';

/**
 * **SCR-WT-006 — expired / invalid link.** The safe dead end.
 *
 * Reached from a token nobody minted (`404 token-unknown`), one that has timed
 * out, been used or been revoked when the trip ended (`410`), a URL with no token
 * on it at all, and a token whose shape says it was never minted here. All four
 * land on this page, and telling them apart would make the surface an oracle over
 * which links are live.
 *
 * **The whole point of this file is what it does not receive.** It takes a
 * translator, a locale and a store URL — no snapshot, no ride, no driver, no
 * token. That is C117's Definition of Done item "an expired or unknown token
 * renders SCR-WT-006 with no ride data in the DOM or network payload" held as a
 * property of the component's signature rather than as care taken by its caller:
 * there is nothing here that *could* render a ride. public-bff holds the other
 * half — the 404/410 is produced before any ride row is read, so the payload the
 * page never renders was never fetched either.
 *
 * `<a>` rather than `<Button>`: the control leaves this origin for an app store,
 * and a `<button>` that navigates is a button that does not work when JavaScript
 * has not loaded — which on a phone opening an SMS link is the first second of
 * every visit. `CTA_CLASS_NAMES` is the preset's own CTA token, published for
 * exactly this case.
 */
export function DeadEnd({
  t,
  locale,
  here,
  appUrl,
}: {
  t: WebTranslator;
  locale: Locale;
  here: string;
  appUrl: string | null;
}) {
  return (
    <Shell t={t} locale={locale} titleKey="web.appName" here={here} centred>
      <span
        aria-hidden="true"
        className="grid size-[74px] place-items-center rounded-lg bg-surface-variant text-[32px]"
      >
        ⏱
      </span>

      <h2 className="text-title font-display">{t('web.expired.title')}</h2>
      <p className="text-body-sm text-on-surface-variant">{t('web.expired.body')}</p>

      {appUrl ? (
        <a href={appUrl} rel="noreferrer noopener external" className={`${CTA_CLASS_NAMES} w-full`}>
          {t('web.expired.open')}
        </a>
      ) : null}
    </Shell>
  );
}
