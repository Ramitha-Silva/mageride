'use client';

import { useMemo, useState, useTransition } from 'react';
import { useRouter } from 'next/navigation';

import { Button, Modal } from '@mageride/ui';

import { createWebTranslator, type Locale, type WebMessageKey } from '@/i18n';
import { raiseWebSos } from '@/server/track-actions';

import { useLiveFeed } from './LiveFeed';

/**
 * **SCR-WT-004's SOS** — US-25.5, D-33, D6' I-29.4.
 *
 * Dual-gateway SMS to the **booker** in parallel plus the admin live feed, written
 * as `safety.sos_events(source='web')`. This is the one place a token-only caller
 * may write a safety event.
 *
 * ## Two presses, and the second one is the alert
 *
 * The wireframe draws SOS as one of a pair of buttons under the driver card — a
 * thumb's width from "Call driver", on a phone, in a moving vehicle. A single press
 * that SMSed somebody would be triggered by accident, and a false alert on a
 * dual-gateway rail is not free: it costs two messages, an entry on the admin live
 * feed and the booker's attention. The dialog is the confirmation, and it says what
 * pressing it does before it is pressed.
 *
 * ## The location is the reader's, and the fallback is named on the screen
 *
 * safety-svc's `sos_events.lat/lng` are `NOT NULL` and public-bff refuses to invent
 * them: "the row must say where the *person* said they were, not where the car
 * was". So the browser's Geolocation API is asked first. D6' I-29.4 gives one
 * fallback — "last driver-reported position" — and on a proxy ride that is a
 * defensible stand-in, because the person reading this page is in that vehicle.
 * It is used only when the browser refuses, and **the screen says which of the two
 * was sent**, because an alert whose coordinates are the car's rather than the
 * rider's is a different fact and whoever acts on it should know.
 *
 * With neither, nothing is sent. An alert that cannot say where somebody is would
 * answer `202` and read as help on the way.
 *
 * ## The outcome is `smsStatus`, not "sent"
 *
 * safety-svc distinguishes `Dispatched`, `Failed` and `NoContact`, and public-bff
 * carries all three because "without the status a caller cannot tell 'the alert
 * went out' from 'the alert is on the admin console and nowhere else'". On a panic
 * button that is the whole difference, so all three get their own sentence and two
 * of them tell the reader to call for help directly as well.
 */

export interface SosButtonProps {
  readonly token: string;
  readonly locale: Locale;
}

interface Result {
  readonly key: WebMessageKey;
  readonly tone: 'success' | 'warning' | 'error';
  /** Set when the driver's position stood in for the reader's. */
  readonly usedDriverPosition?: boolean;
}

/** Long enough for a cold GPS fix, short enough that a panic button still answers. */
const GEOLOCATION_TIMEOUT_MS = 8000;

export function SosButton({ token, locale }: SosButtonProps) {
  const t = useMemo(() => createWebTranslator(locale), [locale]);
  const router = useRouter();

  // D6' I-29.4's fallback, read from the **live** feed rather than from the render
  // this island was mounted in. A page somebody has been watching for a quarter of
  // an hour would otherwise fall back to where the car was when they opened it.
  const { position: driverPosition } = useLiveFeed();

  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<Result | null>(null);
  const [pending, startTransition] = useTransition();

  const send = () => {
    startTransition(async () => {
      const fix = await currentPosition();
      const usedDriverPosition = fix === null;

      const point = fix ?? (driverPosition ? { lat: driverPosition.lat, lng: driverPosition.lng } : null);

      if (!point) {
        setResult({ key: 'web.ride.sosNoPosition', tone: 'error' });
        return;
      }

      const outcome = await raiseWebSos(token, point);

      if (outcome.dead) {
        router.refresh();
        return;
      }
      if (!outcome.ok) {
        setResult({ key: outcome.messageKey ?? 'web.error.unexpected', tone: 'error' });
        return;
      }

      setOpen(false);
      setResult({
        key:
          outcome.smsStatus === 'Dispatched'
            ? 'web.ride.sosSent'
            : outcome.smsStatus === 'NoContact'
              ? 'web.ride.sosNoContact'
              : 'web.ride.sosFailed',
        tone: outcome.smsStatus === 'Dispatched' ? 'success' : 'warning',
        usedDriverPosition,
      });
    });
  };

  return (
    <>
      <Button
        variant="secondary"
        onClick={() => {
          setResult(null);
          setOpen(true);
        }}
        aria-label={t('web.ride.sosOpen')}
        className="flex-1 border-error text-error"
      >
        <span aria-hidden="true">⛨</span>
        {t('web.ride.sos')}
      </Button>

      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t('web.ride.sosTitle')}
        description={t('web.ride.sosBody')}
        closeLabel={t('common.close')}
        footer={
          <>
            <Button variant="ghost" size="compact" onClick={() => setOpen(false)} disabled={pending}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="danger"
              size="compact"
              onClick={send}
              busy={pending}
              busyLabel={t('web.ride.sosSending')}
            >
              {t('web.ride.sosSend')}
            </Button>
          </>
        }
      >
        {result && result.tone === 'error' ? (
          <p role="alert" className="text-body-sm text-error">
            {t(result.key)}
          </p>
        ) : null}
      </Modal>

      {result && !open ? (
        <p
          role="status"
          className={`w-full text-caption ${result.tone === 'success' ? 'text-success' : 'text-warning'}`}
        >
          {t(result.key)}
          {result.usedDriverPosition ? ` ${t('web.ride.sosUsedDriverPosition')}` : ''}
        </p>
      ) : null}
    </>
  );
}

/**
 * The browser's fix, or `null` when it will not give one.
 *
 * Deliberately never rejects. A refused permission, a device with no GPS and a
 * timeout are three ways of arriving at the same place — there is no position —
 * and the caller's next move is the same for all three.
 */
function currentPosition(): Promise<{ lat: number; lng: number; accuracy?: number } | null> {
  if (typeof navigator === 'undefined' || !navigator.geolocation) return Promise.resolve(null);

  return new Promise((resolve) => {
    navigator.geolocation.getCurrentPosition(
      (fix) =>
        resolve({
          lat: fix.coords.latitude,
          lng: fix.coords.longitude,
          ...(Number.isFinite(fix.coords.accuracy) ? { accuracy: fix.coords.accuracy } : {}),
        }),
      () => resolve(null),
      { enableHighAccuracy: true, timeout: GEOLOCATION_TIMEOUT_MS, maximumAge: 0 },
    );
  });
}
