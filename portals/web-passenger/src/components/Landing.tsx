import Link from 'next/link';

import { CTA_CLASS_NAMES } from '@mageride/ui';

import type { PackageSnapshot } from '@/api/types';
import type { Locale, WebTranslator } from '@/i18n';

import { Shell } from './Shell';

/**
 * **SCR-WT-001 — landing / token gate.** US-25.1.
 *
 * The screen is two things at once and the wireframe only draws the second.
 *
 * The **gate** is the page it sits on: `app/track/page.tsx` redeems the token on
 * the server before this component exists, so "no data is rendered before
 * validation" is not a rule this file follows — there is no render in which the
 * token has not already answered. An invalid, expired or revoked one never reaches
 * here at all; it reaches SCR-WT-006.
 *
 * The **landing** is what the wireframe draws, and it is the `package_recipient`
 * scope's screen. A recipient gets an SMS out of the blue about a parcel they did
 * not order, so they get a sentence explaining what has happened and a button they
 * choose to press. The other two scopes get no such page and want none: a
 * `pickup_confirm` token has five minutes on it and a proxy rider's car is already
 * moving, so both are routed straight through to their own screen (D2: "valid →
 * route by scope (002 / 003 / 004)").
 *
 * ## The card is thinner than the sketch, because the platform is
 *
 * The wireframe's card reads From / Size / ETA. `PackageSnapshot` carries a sender
 * display name and no parcel size — nothing in `rides.rides` records one — and no
 * ETA: `etaMin` exists on the **proxy-rider** variant only, so a recipient is never
 * told a minute figure by this contract. The card therefore carries From, Status
 * and the driver, which are three things the snapshot actually knows. Both
 * omissions are in the C117 handoff.
 */
export function Landing({
  t,
  locale,
  here,
  token,
  snapshot,
}: {
  t: WebTranslator;
  locale: Locale;
  here: string;
  token: string;
  snapshot: PackageSnapshot;
}) {
  const sender = snapshot.senderNameMasked ?? t('web.landing.unknownSender');

  return (
    <Shell t={t} locale={locale} titleKey="web.appName" here={here} centred>
      <span
        aria-hidden="true"
        className="grid size-[74px] place-items-center rounded-lg bg-surface-variant text-[32px]"
      >
        📦
      </span>

      <h2 className="text-headline font-display">{t('web.landing.title')}</h2>
      <p className="text-body-sm text-on-surface-variant">
        {t('web.landing.body', { driver: snapshot.driver.name })}
      </p>

      <dl className="w-full rounded-md border border-outline bg-surface p-sm text-start">
        <div className="flex justify-between gap-sm py-xxs text-body-sm">
          <dt className="text-on-surface-variant">{t('web.landing.from')}</dt>
          <dd className="truncate font-semibold">{sender}</dd>
        </div>
        <div className="flex justify-between gap-sm py-xxs text-body-sm">
          <dt className="text-on-surface-variant">{t('web.landing.status')}</dt>
          <dd className="truncate font-semibold">{t(`web.status.${snapshot.status}`)}</dd>
        </div>
        <div className="flex justify-between gap-sm py-xxs text-body-sm">
          <dt className="text-on-surface-variant">{t('web.landing.driver')}</dt>
          <dd className="truncate font-semibold">{snapshot.driver.name}</dd>
        </div>
      </dl>

      {/*
        A `Link`, not a button: the tracking screen is a URL of its own (the
        wireframe's `/t/…`), so the browser's back button works, the page can be
        reloaded, and nothing about reaching it depends on JavaScript having loaded
        on a phone that has just opened an SMS.
      */}
      <Link href={`/t/${token}`} className={`${CTA_CLASS_NAMES} w-full`}>
        {t('web.landing.track')}
      </Link>

      <p className="text-caption text-on-surface-variant">{t('web.landing.expiry')}</p>
    </Shell>
  );
}
