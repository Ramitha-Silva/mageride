'use client';

import { useCallback, useEffect, useMemo, useState, useTransition } from 'react';
import { useRouter } from 'next/navigation';

import { Button } from '@mageride/ui';

import { createWebTranslator, type Locale, type WebMessageKey } from '@/i18n';
import { formatCountdown } from '@/i18n/format';
import { declinePickupLocation, sharePickupLocation } from '@/server/track-actions';

import { LazyTrackMap } from './LazyTrackMap';

/**
 * **SCR-WT-003 — confirm pickup (unregistered proxy rider).** US-25.3, AL-45, P-02.
 *
 * The screen an unregistered rider reaches from an SMS when the person booking for
 * them hits `RiderNotRegistered`. It has five minutes, one map pin and two buttons,
 * and the whole of it is one promise: **declining transmits no GPS**.
 *
 * ## Three things hold that promise, and only one of them is this file
 *
 * The banner says it before the reader decides anything (the wireframe puts it in
 * the same breath as the countdown). {@link declinePickupLocation} takes no
 * coordinate. public-bff's handler reads no body and ride-svc's statement has no
 * `resolved_geo` in its `SET` list. This component adds a fourth property that is
 * worth stating: **the browser's geolocation API is not called until the reader
 * asks for it.** Nothing here fires `getCurrentPosition` on mount — a page that
 * quietly prompted for location the moment it opened would have taken a position
 * before the reader had read the sentence promising it would not.
 *
 * ## The countdown is anchored to the server's remaining seconds, not to a clock
 *
 * `ttlRemainingSec` is public-bff's own arithmetic over `issued_at + ttl_seconds`
 * — the durable fact ride-svc's sweep reads too. A countdown computed from
 * `expiresAt` minus `Date.now()` would be wrong by however far the phone's clock
 * has drifted, which on a five-minute window is the difference between a rider who
 * thinks they have a minute left and a request that closed thirty seconds ago.
 *
 * ## Expiry closes the screen here and the request closes it there
 *
 * When the countdown reaches zero the controls go and the expired copy takes their
 * place — but the *authority* is still the server: pressing a stale button answers
 * `410` and the page refreshes into SCR-WT-006. This is the cheap half, done so the
 * reader is not left pressing a button that cannot work.
 */

export interface PickupConfirmProps {
  readonly token: string;
  readonly locale: Locale;
  readonly styleUrl: string | null;
  /** First name only. P-02 — a rider being asked where they are is owed no more. */
  readonly bookerFirstName: string;
  readonly suggestedPin?: { readonly lat: number; readonly lng: number } | undefined;
  /** public-bff's own countdown at render time. The clock starts from this. */
  readonly ttlRemainingSec: number;
}

type Resolution = 'shared' | 'declined' | null;

export function PickupConfirm({
  token,
  locale,
  styleUrl,
  bookerFirstName,
  suggestedPin,
  ttlRemainingSec,
}: PickupConfirmProps) {
  const t = useMemo(() => createWebTranslator(locale), [locale]);
  const router = useRouter();

  const [remaining, setRemaining] = useState(Math.max(0, ttlRemainingSec));
  const [pin, setPin] = useState(suggestedPin ?? null);
  /** Set only when the pin *is* the browser's fix, so `accuracy` describes it. */
  const [accuracy, setAccuracy] = useState<number | null>(null);
  const [locating, setLocating] = useState(false);
  const [resolution, setResolution] = useState<Resolution>(null);
  const [notice, setNotice] = useState<WebMessageKey | null>(null);
  const [inFlight, setInFlight] = useState<'share' | 'decline' | null>(null);
  const [pending, startTransition] = useTransition();

  // Anchored at mount: `Date.now()` is used for the *interval* between ticks, never
  // as the origin. A phone whose clock is an hour out still counts five minutes.
  //
  // The timer is never cleared on reaching zero and does not need to be — the
  // clamp makes every tick after that set the same value, and React bails out of a
  // render when the state is `Object.is`-equal to what it already holds.
  useEffect(() => {
    const timer = setInterval(() => setRemaining((left) => Math.max(0, left - 1)), 1000);
    return () => clearInterval(timer);
  }, []);

  const expired = remaining <= 0;
  const busy = pending || locating;

  const locate = useCallback(() => {
    if (!navigator.geolocation) {
      setNotice('web.pickup.locationDenied');
      return;
    }

    setLocating(true);
    setNotice(null);
    navigator.geolocation.getCurrentPosition(
      (fix) => {
        setPin({ lat: fix.coords.latitude, lng: fix.coords.longitude });
        setAccuracy(Number.isFinite(fix.coords.accuracy) ? fix.coords.accuracy : null);
        setLocating(false);
      },
      () => {
        // A refusal is a decision, not a failure: the reader is told the pin is
        // theirs to place instead, and nothing was sent either way.
        setNotice('web.pickup.locationDenied');
        setLocating(false);
      },
      { enableHighAccuracy: true, timeout: 10_000, maximumAge: 0 },
    );
  }, []);

  const movePin = useCallback((point: { lng: number; lat: number }) => {
    setPin(point);
    // The metres the browser reported described the fix, not the place the reader
    // has just dragged the pin to. Sending it on would be a made-up figure about a
    // point nothing measured.
    setAccuracy(null);
  }, []);

  const share = () => {
    if (!pin) return;

    setInFlight('share');
    startTransition(async () => {
      const outcome = await sharePickupLocation(token, {
        lat: pin.lat,
        lng: pin.lng,
        ...(accuracy === null ? {} : { accuracy }),
      });
      setInFlight(null);

      if (outcome.dead) {
        router.refresh();
        return;
      }
      if (!outcome.ok) {
        setNotice(outcome.messageKey ?? 'web.error.unexpected');
        return;
      }
      setResolution('shared');
    });
  };

  const decline = () => {
    setInFlight('decline');
    startTransition(async () => {
      const outcome = await declinePickupLocation(token);
      setInFlight(null);

      if (outcome.dead) {
        router.refresh();
        return;
      }
      if (!outcome.ok) {
        setNotice(outcome.messageKey ?? 'web.error.unexpected');
        return;
      }
      setResolution('declined');
    });
  };

  if (resolution) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-sm text-center">
        <span
          aria-hidden="true"
          className={`grid size-[74px] place-items-center rounded-full text-[32px] ${
            resolution === 'shared' ? 'bg-success/12 text-success' : 'bg-surface-variant text-on-surface-variant'
          }`}
        >
          {resolution === 'shared' ? '✓' : '⊘'}
        </span>
        <p className="text-body font-semibold">
          {resolution === 'shared'
            ? t('web.pickup.shared', { name: bookerFirstName })
            : t('web.pickup.declined')}
        </p>
        <p className="text-caption text-on-surface-variant">{t('web.pickup.closed')}</p>
      </div>
    );
  }

  if (expired) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-sm text-center">
        <span aria-hidden="true" className="grid size-[74px] place-items-center rounded-lg bg-surface-variant text-[32px]">
          ⏱
        </span>
        <h2 className="text-title font-display">{t('web.pickup.expiredTitle')}</h2>
        <p className="text-body-sm text-on-surface-variant">
          {t('web.pickup.expiredBody', { name: bookerFirstName })}
        </p>
      </div>
    );
  }

  return (
    <>
      {/*
        The wireframe's warning banner, and both halves of it matter: the countdown
        is why the reader should decide now, and the promise is what lets them
        decide either way. `aria-live="off"` on purpose — a countdown announced
        every second by a screen reader would make the page unusable; the sentence
        beside it does not change.
      */}
      <p className="-mx-md -mt-md flex items-center gap-xs bg-warning/15 px-md py-xs text-caption font-semibold text-on-surface">
        <span aria-hidden="true">⏱</span>
        <span>
          {t('web.pickup.expiresIn', { clock: formatCountdown(remaining) })} · {t('web.pickup.noGps')}
        </span>
      </p>

      <LazyTrackMap
        styleUrl={styleUrl}
        markers={[]}
        {...(pin ? { pin } : {})}
        onPinMove={movePin}
        className="h-[240px]"
        labels={{
          region: t('web.pickup.mapLabel'),
          noBasemap: t('web.map.noBasemap'),
          zoomIn: t('web.map.zoomIn'),
          zoomOut: t('web.map.zoomOut'),
          attribution: t('web.map.attribution'),
          metres: t('web.map.metres'),
          kilometres: t('web.map.kilometres'),
          pin: t('web.pickup.pinLabel'),
          dragHint: t('web.pickup.dragHint'),
        }}
      />

      <p className="text-body font-semibold">{t('web.pickup.title', { name: bookerFirstName })}</p>
      <p className="text-caption text-on-surface-variant">{t('web.pickup.body')}</p>

      <Button
        variant="secondary"
        size="compact"
        onClick={locate}
        busy={locating}
        busyLabel={t('web.pickup.locating')}
        className="w-full"
      >
        {t('web.pickup.useMyLocation')}
      </Button>

      {notice ? (
        <p role="alert" className="text-caption text-error">
          {t(notice)}
        </p>
      ) : null}

      {!pin ? <p className="text-caption text-on-surface-variant">{t('web.pickup.noPin')}</p> : null}

      <div className="mt-auto flex gap-xs pt-sm">
        <Button
          variant="secondary"
          onClick={decline}
          disabled={busy}
          busy={inFlight === 'decline'}
          busyLabel={t('web.pickup.declining')}
          className="flex-1"
        >
          {t('web.pickup.decline')}
        </Button>
        <Button
          onClick={share}
          disabled={busy || !pin}
          busy={inFlight === 'share'}
          busyLabel={t('web.pickup.sharing')}
          className="flex-1"
        >
          {t('web.pickup.share')}
        </Button>
      </div>
    </>
  );
}
