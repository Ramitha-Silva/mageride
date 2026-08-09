import type { PackageSnapshot } from '@/api/types';
import type { Locale, WebTranslator } from '@/i18n';

import { DriverCard } from './DriverCard';
import { LiveFeed, LiveMap } from './LiveFeed';
import { OtpDigits } from './OtpDigits';
import { PackageStepper } from './PackageStepper';
import { Shell } from './Shell';

/**
 * **SCR-WT-002 — package track (recipient).** US-25.1/25.2, US-20.5, P-07, P-09.
 *
 * Live driver position, the four-step tracker, the delivery code, the driver card
 * with its `tel:` link, and the app strip. In that order, because that is the order
 * the wireframe puts them in and it is the order a recipient reads them: *where is
 * it, how far along is it, what do I have to say, who is coming.*
 *
 * ## Almost all of it is server-rendered
 *
 * The two client islands are the map and the live pill. The stepper, the code and
 * the driver card come off the server's own snapshot read, so the page is complete
 * — code included — before a single byte of JavaScript has run. On a recipient's
 * phone, opening an SMS on whatever connection they have, that is the difference
 * between a delivery code and a spinner.
 *
 * The feed then updates the marker in place, and the *status* it carries is only
 * used to decide when the screen is over: `resolved` triggers `router.refresh()`
 * and the server re-renders — into SCR-WT-005 if the parcel arrived, into
 * SCR-WT-006 if the token was revoked with it. The stepper is not advanced from the
 * client, because a page that advanced itself to "Delivered" would be claiming a
 * handover the server had not confirmed.
 *
 * ## What is missing from the map, and why
 *
 * The wireframe draws a drop-off pin beside the driver. `PackageSnapshot.dropoff`
 * is **omitted by public-bff on every parcel**: `rides.rides` stores a
 * `dropoff_geo` and no address, and the service will not fill an address-shaped
 * field with coordinates. So there is no drop-off *anything* on this contract —
 * neither a line to print nor a point to draw — and the map carries the vehicle
 * alone. In the C117 handoff.
 */
export function PackageTrack({
  t,
  locale,
  here,
  token,
  snapshot,
  styleUrl,
  appUrl,
}: {
  t: WebTranslator;
  locale: Locale;
  here: string;
  token: string;
  snapshot: PackageSnapshot;
  styleUrl: string | null;
  appUrl: string | null;
}) {
  return (
    <Shell
      t={t}
      locale={locale}
      titleKey="web.bar.package"
      here={here}
      appStrip={{ promptKey: 'web.app.deliveriesPrompt', href: appUrl }}
    >
      <LiveFeed token={token} initialPosition={snapshot.position}>
        <LiveMap
          locale={locale}
          styleUrl={styleUrl}
          regionLabel={t('web.package.progress')}
          mapClassName="h-[200px]"
        />

        <PackageStepper t={t} status={snapshot.status} />

        <OtpDigits
          t={t}
          titleKey="web.package.otpTitle"
          pendingKey="web.package.otpPending"
          code={snapshot.deliveryOtp}
        />

        {snapshot.senderNameMasked ? (
          <p className="text-caption text-on-surface-variant">
            {t('web.package.senderLine', { sender: snapshot.senderNameMasked })}
          </p>
        ) : null}

        <DriverCard t={t} driver={snapshot.driver} />
      </LiveFeed>
    </Shell>
  );
}
