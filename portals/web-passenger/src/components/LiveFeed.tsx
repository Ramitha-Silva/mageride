'use client';

import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { useRouter } from 'next/navigation';

import { createWebTranslator, type Locale } from '@/i18n';
import { formatSince } from '@/i18n/format';
import { useLiveTrack, type LiveConnection } from '@/live/useLiveTrack';
import type { LngLat } from '@/lib/polyline';
import type { TrackedPosition } from '@/api/types';

import { LazyTrackMap } from './LazyTrackMap';
import type { MapMarker } from './TrackMap';

/**
 * One feed per screen, shared by everything on it — SCR-WT-002 and SCR-WT-004.
 *
 * ## Why a provider and not a prop
 *
 * Two things on SCR-WT-004 need the vehicle's current position: the map draws it,
 * and the SOS button falls back to it when the browser refuses to give the
 * reader's own (D6' I-29.4). Handing each its own `useLiveTrack` would open **two**
 * `EventSource` connections against a rate-limited, per-token bucket, and handing
 * the SOS the server-rendered snapshot instead would give it a fix from whenever
 * the page was opened — which on a ride somebody has been watching for fifteen
 * minutes is not where anybody is.
 *
 * So the feed is opened once here and read through context. The children are
 * **server-rendered** and passed straight through: the driver card, the OTP and
 * the fare notice never become client components for this, and the two islands
 * that do consume the feed find it through the tree they are rendered into.
 *
 * ## The screen advances when the feed says the journey is over
 *
 * public-bff emits `resolved` as a separate frame from the status that says so,
 * because the two mean different things to a page: the status advances the tracker,
 * and `resolved` is what sends SCR-WT-002 to SCR-WT-005 and tells the client to
 * stop reconnecting. On it, `router.refresh()` re-runs the **server** render —
 * which re-reads the token, so a link revoked at trip end becomes SCR-WT-006 rather
 * than a page still holding a driver's name. A client-side transition to a receipt
 * would be the page deciding it is entitled to data the server has not re-checked.
 *
 * Fired once, guarded by state rather than by the effect's dependencies:
 * `router.refresh()` does not change `closed`, so the effect would otherwise fire
 * on every subsequent render.
 */

export interface LiveFeedValue {
  readonly position: TrackedPosition | null;
  readonly status: string | null;
  readonly connection: LiveConnection;
  readonly closed: boolean;
}

const LiveFeedContext = createContext<LiveFeedValue>({
  position: null,
  status: null,
  connection: 'connecting',
  closed: false,
});

export function useLiveFeed(): LiveFeedValue {
  return useContext(LiveFeedContext);
}

export function LiveFeed({
  token,
  initialPosition,
  children,
}: {
  readonly token: string;
  /** The fix the server rendered with, if it had one. */
  readonly initialPosition?: TrackedPosition | undefined;
  readonly children: ReactNode;
}) {
  const router = useRouter();
  const feed = useLiveTrack(token);

  // A ref and not state: this guard exists to make the refresh happen **once**, and
  // it is read only inside the effect. Holding it in state would make setting it a
  // synchronous `setState` in an effect — a cascading render for a boolean nothing
  // renders.
  const refreshed = useRef(false);

  useEffect(() => {
    if (!feed.closed || refreshed.current) return;
    refreshed.current = true;
    router.refresh();
  }, [feed.closed, router]);

  const value = useMemo<LiveFeedValue>(
    () => ({
      position: feed.position ?? initialPosition ?? null,
      status: feed.status,
      connection: feed.connection,
      closed: feed.closed,
    }),
    [feed.position, feed.status, feed.connection, feed.closed, initialPosition],
  );

  return <LiveFeedContext.Provider value={value}>{children}</LiveFeedContext.Provider>;
}

/**
 * The map and the one line that says whether the feed is still there.
 *
 * **A client component takes a locale, not thirty label props.** The translator is
 * a function and React cannot serialise one across the server/client boundary (the
 * Fleet Portal's rule, "a label prop is a string, never a function"). `@/i18n` is
 * framework-free and has no server dependency, so a client component is handed the
 * locale string and builds its own — which is also what gives live copy like
 * "updated 12s ago", which changes without any render from the server, somewhere
 * to come from.
 */
export function LiveMap({
  locale,
  styleUrl,
  regionLabel,
  route,
  endpoints,
  mapClassName,
}: {
  readonly locale: Locale;
  /**
   * D-14's `tile-cdn` — the one URL a browser on this surface fetches, and
   * deliberately **not** a `NEXT_PUBLIC_*` variable. It is read on the server and
   * handed down as a prop, so the client bundle carries no configuration at all
   * and the same image serves different cartography on the replica and on DOKS.
   */
  readonly styleUrl: string | null;
  readonly regionLabel: string;
  /** SCR-WT-004's trip line, decoded on the server so the browser decodes nothing. */
  readonly route?: readonly LngLat[] | undefined;
  /** Fixed points the journey has beyond the vehicle — a pickup, a drop-off. */
  readonly endpoints?: readonly MapMarker[] | undefined;
  readonly mapClassName?: string | undefined;
}) {
  const t = useMemo(() => createWebTranslator(locale), [locale]);
  const { position, connection } = useLiveFeed();

  // A relative stamp has to be recomputed even when nothing arrives — a vehicle
  // parked at a junction reports nothing, and "updated 3s ago" frozen on the screen
  // is the exact lie this line exists to prevent.
  const [tick, setTick] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setTick(Date.now()), 10_000);
    return () => clearInterval(timer);
  }, []);

  const markers: MapMarker[] = [
    ...(endpoints ?? []),
    ...(position ? [{ id: 'driver', kind: 'driver' as const, lng: position.lng, lat: position.lat }] : []),
  ];

  return (
    <div className="print-hidden flex flex-col gap-xxs">
      <LazyTrackMap
        styleUrl={styleUrl}
        markers={markers}
        {...(route ? { route } : {})}
        {...(mapClassName ? { className: mapClassName } : {})}
        labels={{
          region: regionLabel,
          noBasemap: t('web.map.noBasemap'),
          zoomIn: t('web.map.zoomIn'),
          zoomOut: t('web.map.zoomOut'),
          attribution: t('web.map.attribution'),
          metres: t('web.map.metres'),
          kilometres: t('web.map.kilometres'),
        }}
      />

      <p aria-live="polite" className="flex flex-wrap items-center gap-xxs text-caption text-on-surface-variant">
        <span
          aria-hidden="true"
          className={`block size-xxs shrink-0 rounded-full ${
            connection === 'live' || connection === 'polling'
              ? 'bg-success'
              : connection === 'closed'
                ? 'bg-outline'
                : 'bg-warning'
          }`}
        />
        <span>
          {connection === 'closed'
            ? t('web.live.stopped')
            : connection === 'reconnecting' || connection === 'connecting'
              ? t('web.live.reconnecting')
              : t('web.live.on')}
        </span>
        <span>
          ·{' '}
          {position
            ? t('web.live.lastFix', { since: formatSince(locale, position.ts, tick) ?? '' })
            : t('web.live.noPosition')}
        </span>
      </p>
    </div>
  );
}
