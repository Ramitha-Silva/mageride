import 'server-only';

import type { ReactNode } from 'react';

import { isDeadToken } from '@/api/problem';
import { isWellFormedToken, readReceipt, readSnapshot } from '@/api/track';
import { DeadEnd } from '@/components/DeadEnd';
import { DeliveredReceipt } from '@/components/DeliveredReceipt';
import { Landing } from '@/components/Landing';
import { PackageTrack } from '@/components/PackageTrack';
import { PickupConfirm } from '@/components/PickupConfirm';
import { RideTrack } from '@/components/RideTrack';
import { Shell } from '@/components/Shell';
import { mapStyleUrl } from '@/config/env';

import { screenContext, type SearchParams } from './render';

/**
 * `RideStates.JourneyOver` — `PaymentPending` onwards, the window in which the ride
 * is over even though the money may not be. Transcribed from public-bff's own
 * `PublicVocabulary`, because the two have to agree about when a tracker stops
 * being the right screen.
 */
const RIDE_JOURNEY_OVER = new Set([
  'PaymentPending',
  'Paid',
  'CashSettled',
  'CashOnDeliveryCollected',
  'Disputed',
]);

/**
 * The token gate, and the dispatch D2 §SCR-WT-001 describes: "validates `?token=`
 * → route by scope (002 / 003 / 004); expired/invalid → 006; already-delivered →
 * 005 receipt view."
 *
 * One function, called by all three route files, because it is one decision. Three
 * copies of "redeem the token, then work out which screen this is" would be three
 * places for the dead-token branch to be forgotten, and the dead-token branch is
 * the one C117's Definition of Done is about.
 *
 * ## It routes by rendering, not by redirecting
 *
 * An earlier shape sent the two proxy scopes to `/p/{token}` with `redirect()`. It
 * worked, and it was wrong here: the route streams a `loading.tsx` spinner while
 * the token is being redeemed (D2's "≤ 1 s"), so by the time the scope is known the
 * response headers have gone and Next can only finish a redirect as a meta refresh.
 * A proxy rider whose car is already moving would have watched a spinner, a blank,
 * and a second spinner. Rendering the screen in place costs one page and no hops,
 * and the wireframe's two URL shapes — `/t/…` for the parcel, `/p/…` for the proxy
 * pair — still exist and still work, because all three routes call this.
 *
 * ## An async function, not an async component
 *
 * The pages `return trackScreen(...)`. A tree with an unresolved async child in it
 * renders as nothing under `@testing-library`, which makes it a test that cannot be
 * written rather than a test that fails — the Fleet Portal learned the same thing
 * on its dashboard.
 */
export async function trackScreen({
  token,
  pathname,
  searchParams,
  landing = false,
}: {
  readonly token: string | undefined;
  readonly pathname: string;
  readonly searchParams: SearchParams;
  /**
   * Whether a package recipient gets the SCR-WT-001 landing card before the
   * tracker.
   *
   * True at `/track?token=…` — the URL notification-svc puts in the SMS — because a
   * recipient did not order this parcel and deserves a sentence explaining what has
   * happened before a map. False at `/t/{token}`, which is where the landing's own
   * button goes. The other two scopes never see it: a `pickup_confirm` token has
   * five minutes on it and a proxy rider's car is already moving.
   */
  readonly landing?: boolean;
}): Promise<ReactNode> {
  const { locale, t, here, appUrl } = await screenContext(pathname, searchParams);
  const dead = <DeadEnd t={t} locale={locale} here={here} appUrl={appUrl} />;

  // A value that was never minted is refused on shape, before it costs public-bff a
  // Redis round trip or spends a real visitor's per-IP rate budget. It reaches the
  // same page an expired token does: telling the two apart would make the surface
  // an oracle over which links are live.
  if (!isWellFormedToken(token)) return dead;

  let snapshot;
  try {
    snapshot = await readSnapshot(token);
  } catch (error) {
    if (isDeadToken(error)) return dead;
    throw error;
  }

  if (snapshot.kind === 'ride') {
    // **A proxy ride can end while its link is still live**, and D2 heads SCR-WT-005
    // "Delivered / Trip Summary" for both kinds. In practice safety-svc closes a
    // trip-scoped token at trip end, so this is usually reached as SCR-WT-006 —
    // but while the token *is* live, a summary is a better answer than a tracker
    // watching a vehicle that has stopped.
    //
    // The receipt itself decides whether there is one: public-bff's `Receiptable`
    // is narrower than "terminal" on purpose — six of D5' §6's ten terminals are
    // cancellations and no-shows, and a journey that did not happen has no receipt.
    // A `null` therefore falls through to the tracker, whose chip already says the
    // ride has ended, rather than to a summary claiming a trip that never ran.
    if (RIDE_JOURNEY_OVER.has(snapshot.state)) {
      const receipt = await readReceipt(token);

      if (receipt) {
        return (
          <DeliveredReceipt
            t={t}
            locale={locale}
            here={here}
            kind="ride"
            driver={snapshot.driver}
            receipt={receipt}
            appUrl={appUrl}
          />
        );
      }
    }

    return (
      <RideTrack
        t={t}
        locale={locale}
        here={here}
        token={token}
        snapshot={snapshot}
        styleUrl={mapStyleUrl()}
      />
    );
  }

  if (snapshot.kind === 'pickup_confirm') {
    // The shell is server-rendered and the screen inside it is a client island,
    // because everything on SCR-WT-003 moves: a countdown, a draggable pin, a
    // permission prompt and two writes.
    return (
      <Shell t={t} locale={locale} titleKey="web.bar.pickup" here={here}>
        <PickupConfirm
          token={token}
          locale={locale}
          styleUrl={mapStyleUrl()}
          bookerFirstName={snapshot.bookerFirstName}
          suggestedPin={snapshot.suggestedPin}
          ttlRemainingSec={snapshot.ttlRemainingSec}
        />
      </Shell>
    );
  }

  if (snapshot.status === 'Delivered') {
    // `null` while the handover has happened and the money has not settled —
    // public-bff's `Receiptable` is narrower than "terminal", and `409
    // receipt-not-ready` is a state rather than a failure. The screen says so.
    const receipt = await readReceipt(token);

    return (
      <DeliveredReceipt
        t={t}
        locale={locale}
        here={here}
        kind="package"
        driver={snapshot.driver}
        senderName={snapshot.senderNameMasked}
        receipt={receipt}
        appUrl={appUrl}
      />
    );
  }

  if (landing) {
    return <Landing t={t} locale={locale} here={here} token={token} snapshot={snapshot} />;
  }

  return (
    <PackageTrack
      t={t}
      locale={locale}
      here={here}
      token={token}
      snapshot={snapshot}
      styleUrl={mapStyleUrl()}
      appUrl={appUrl}
    />
  );
}
