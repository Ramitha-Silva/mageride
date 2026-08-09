import { StatusPill } from '@mageride/ui';

import type { ProxyRideSnapshot } from '@/api/types';
import { formatAmountMinor } from '@/i18n/format';
import type { Locale, WebMessageKey, WebTranslator } from '@/i18n';
import { decodePolyline } from '@/lib/polyline';

import { DriverCard } from './DriverCard';
import { LiveFeed, LiveMap } from './LiveFeed';
import { OtpDigits } from './OtpDigits';
import { Shell } from './Shell';
import { SosButton } from './SosButton';

/**
 * **SCR-WT-004 — ride track (proxy rider).** US-25.1/25.4/25.5, US-8.21/8.22.
 *
 * Somebody else booked this car. The reader is in it, has no MageRide account, and
 * needs four things: where the driver is, who they are, whether any money is owed
 * at the end, and a way to raise an alarm.
 *
 * ## The state chip is the ride's, translated — never re-derived
 *
 * `_shared.yaml#RideState` has eighteen values and this screen can be opened in any
 * of them. They are collapsed to five sentences because a rider does not need
 * eighteen, and the collapse is a lookup rather than arithmetic: whatever ride-svc
 * says the ride is, is what the chip says.
 *
 * ## The Start OTP card is drawn only if there is a code, and there never is
 *
 * `ride.yaml` and ride-svc's own `RideContracts` both say a rider start OTP is
 * "accepted and ignored in this build: no endpoint issues one", and the two OTP
 * columns on `rides.rides` are package-only. public-bff therefore omits `startOtp`
 * on every proxy ride rather than inventing four digits no driver could be asked to
 * check. The card renders the sentence instead — a deliverable that names a control
 * deserves an explanation rather than a silent gap. Recorded in the C117 handoff.
 *
 * ## What the reader is told about the money, and what they are not
 *
 * `cash_due` is US-8.21's notice: the booker chose cash, so the fare is owed by
 * whoever is in the car — which is the person reading this. Everything else settles
 * against the booker's own instrument, and **which** instrument is not said,
 * because `PublicFareResponse` has no field that could say it (P-09). A ride with
 * no quote carries no fare block at all rather than a zero, and this renders
 * nothing for that case: "Rs 0.00" on a tracking page reads as "this is free".
 */

/**
 * The eighteen ride states, as five sentences.
 *
 * `arriving` covers everything before the rider is aboard, `onTrip` covers the
 * journey, and every terminal — completed, cancelled, no-show — is "this ride has
 * ended", because from this page they are the same fact: there is nothing more to
 * watch. Two of the five have a minutes variant, used only when public-bff supplied
 * one (it omits `etaMin` when there is no fresh position, when the journey is over,
 * and when the estimate exceeds its own ceiling).
 */
function stateLine(state: string, etaMin: number | undefined): {
  key: WebMessageKey;
  tone: 'info' | 'success' | 'neutral';
} {
  switch (state) {
    case 'Requested':
    case 'Matching':
    case 'Offered':
      return { key: 'web.ride.state.matching', tone: 'neutral' };
    case 'Accepted':
      return {
        key: etaMin === undefined ? 'web.ride.state.arriving' : 'web.ride.state.arrivingIn',
        tone: 'info',
      };
    case 'DriverArrived':
      return { key: 'web.ride.state.waiting', tone: 'success' };
    case 'InProgress':
      return {
        key: etaMin === undefined ? 'web.ride.state.onTrip' : 'web.ride.state.onTripIn',
        tone: 'info',
      };
    default:
      return { key: 'web.ride.state.ended', tone: 'neutral' };
  }
}

export function RideTrack({
  t,
  locale,
  here,
  token,
  snapshot,
  styleUrl,
}: {
  t: WebTranslator;
  locale: Locale;
  here: string;
  token: string;
  snapshot: ProxyRideSnapshot;
  styleUrl: string | null;
}) {
  const { key: stateKey, tone } = stateLine(snapshot.state, snapshot.etaMin);

  // Decoded on the **server**: the browser gets an array it can hand straight to
  // MapLibre, and the decoder never ships. `route.polyline` is two vertices today
  // (ADD §7.6 puts road routing in Phase 3, so `rides.rides` stores an origin and a
  // destination and no geometry) — a straight line between the ends of the journey,
  // which is what dispatch-svc and query-svc also only have.
  const route = decodePolyline(snapshot.route?.polyline);

  // The pickup is the first vertex of that line — the one fixed point on the map
  // besides the vehicle. The drop-off is the last, and is drawn once the rider is
  // aboard: before that, the thing they are watching for is the car.
  const endpoints = route.length >= 2
    ? [
        { id: 'pickup', kind: 'pickup' as const, lng: route[0]![0], lat: route[0]![1] },
        ...(snapshot.state === 'InProgress'
          ? [
              {
                id: 'dropoff',
                kind: 'dropoff' as const,
                lng: route[route.length - 1]![0],
                lat: route[route.length - 1]![1],
              },
            ]
          : []),
      ]
    : [];

  return (
    <Shell t={t} locale={locale} titleKey="web.bar.ride" here={here}>
      <LiveFeed token={token} initialPosition={snapshot.position}>
        <LiveMap
          locale={locale}
          styleUrl={styleUrl}
          regionLabel={t('web.ride.mapLabel')}
          route={route}
          endpoints={endpoints}
          mapClassName="h-[200px]"
        />

        <div className="flex flex-wrap items-center gap-xs">
          <StatusPill tone={tone}>
            {t(stateKey, snapshot.etaMin === undefined ? undefined : { minutes: snapshot.etaMin })}
          </StatusPill>
          {/* D2 asks for a "third-party booking" context chip, and it is the one
              piece of context this reader does not have: they did not book this. */}
          <StatusPill tone="neutral" dot={false}>
            {t('web.ride.thirdParty')}
          </StatusPill>
        </div>

        {/* No call control on the card: the wireframe puts "📞 Call driver" in the
            button row below, beside SOS. */}
        <DriverCard t={t} driver={snapshot.driver} variant="card" showCall={false} />

        <OtpDigits
          t={t}
          titleKey="web.ride.startOtpTitle"
          pendingKey="web.ride.noStartOtp"
          code={snapshot.startOtp}
        />

        {snapshot.fare ? (
          <p
            className={`rounded-md p-sm text-body-sm ${
              snapshot.fare.paidBy === 'cash_due'
                ? 'bg-warning/15 font-semibold text-on-surface'
                : 'bg-surface text-on-surface-variant'
            }`}
          >
            {t(snapshot.fare.paidBy === 'cash_due' ? 'web.ride.cashDue' : 'web.ride.paidByBooker', {
              amount: formatAmountMinor(locale, snapshot.fare.totalMinor),
            })}
          </p>
        ) : null}

        {/*
          The wireframe's button row. "Call driver" is a plain `tel:` link and makes
          no request to MageRide (AL-48 removed `/call`, the proxy-DID lease and the
          confirm-your-number step in full); SOS is the one control on this surface
          that writes a safety event, and it asks twice before it does.
        */}
        <div className="mt-auto flex flex-wrap gap-xs pt-sm">
          {snapshot.driver.phone ? (
            <a
              href={`tel:${snapshot.driver.phone}`}
              aria-label={t('web.driver.callAria', {
                name: snapshot.driver.name,
                phone: snapshot.driver.phone,
              })}
              className="flex h-cta flex-1 items-center justify-center gap-xs rounded-sm border border-outline bg-background px-md text-subtitle font-semibold text-on-surface"
            >
              <span aria-hidden="true">📞</span>
              {t('web.driver.callDriver')}
            </a>
          ) : null}
          <SosButton token={token} locale={locale} />
        </div>
      </LiveFeed>
    </Shell>
  );
}
