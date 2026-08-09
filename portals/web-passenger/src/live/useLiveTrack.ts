'use client';

import { useEffect, useRef, useState } from 'react';

import type { TrackEvent, TrackEventBatch, TrackedPosition } from '@/api/types';

/**
 * The live feed, as the page sees it — `GET /public/track/{token}/live`, proxied
 * same-origin by `app/api/live/[token]/route.ts`.
 *
 * ## Reconnecting is the browser's job, and that is why this is an `EventSource`
 *
 * C117's Definition of Done asks that "the live feed reconnects after a dropped
 * SSE stream without a full reload". An `EventSource` does exactly that with no
 * code: on a dropped connection it waits, reopens, and sends the last `id:` it saw
 * back as `Last-Event-ID`. public-bff writes the cursor into every frame's `id:`
 * and honours the header identically to `?since`, and the proxy route forwards it
 * — so the three pieces already compose into a resume, and the *right*
 * implementation here is the one that does not reimplement it.
 *
 * That matters more than it looks, because a dropped stream is the **normal** case
 * on this feed: `PublicBff:StreamMaxDuration` closes every connection after five
 * minutes so that a revocation reaches somebody who left the tab open. A page that
 * treated a closed stream as a failure would show an error every five minutes on a
 * delivery that is going perfectly.
 *
 * ## The poll fallback is for the connection the fallback exists for
 *
 * D6' I-29.1 asks for "long-poll fallback `?since=cursor` for older browsers", and
 * the real reason is narrower and worse than old browsers: an intermediary that
 * buffers a response body turns SSE into a feed that arrives all at once when it
 * ends. public-bff sets `X-Accel-Buffering: no` and disables Kestrel's own
 * buffering, but neither reaches a corporate proxy or a mobile operator's
 * transcoder. So after two consecutive failures to *open* a stream this switches
 * to polling the same route, with the same cursor, and stays there — flapping
 * between the two transports would be worse than either.
 *
 * ## Nothing here is stored
 *
 * No `localStorage`, no cookie, no history buffer (D6' I-29.1). The hook holds one
 * position and one status — the current ones — which is also all public-bff will
 * ever answer with: the cursor "describes what the client already knows and
 * indexes nothing", because a replayable feed would be the historical replay D-34
 * forbids reached through the back door.
 */

export type LiveConnection = 'connecting' | 'live' | 'polling' | 'reconnecting' | 'closed';

export interface LiveTrack {
  /** The latest fix, or `null` until one arrives. Never a stale one — public-bff omits those. */
  readonly position: TrackedPosition | null;
  /** The latest status the feed reported: a `PackageStatus`, or a `RideState`. */
  readonly status: string | null;
  readonly connection: LiveConnection;
  /** The feed said the journey is over. The page reloads itself once on this. */
  readonly closed: boolean;
}

/** How many failed opens before the socket is given up on for good. */
const STREAM_ATTEMPTS = 2;

/** Inside D6' §5.1's 1–3 s band, and the same read interval the stream uses. */
const POLL_INTERVAL_MS = 3000;

export function useLiveTrack(token: string, enabled = true): LiveTrack {
  const [position, setPosition] = useState<TrackedPosition | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [connection, setConnection] = useState<LiveConnection>('connecting');
  const [closed, setClosed] = useState(false);

  // The cursor is a ref rather than state: it changes on every frame and nothing
  // renders it, so putting it in state would re-render the map for a string.
  const cursor = useRef<string | null>(null);

  useEffect(() => {
    if (!enabled || !token) return undefined;

    const base = `/api/live/${encodeURIComponent(token)}`;
    let cancelled = false;
    let source: EventSource | null = null;
    let poller: ReturnType<typeof setTimeout> | null = null;
    let polling = false;
    let failedOpens = 0;

    const apply = (event: TrackEvent) => {
      if (event.cursor) cursor.current = event.cursor;
      if (event.position) setPosition(event.position);
      if (event.status) setStatus(event.status);

      if (event.type === 'resolved') {
        setClosed(true);
        setConnection('closed');
      }
    };

    const stopEverything = () => {
      polling = false;
      source?.close();
      source = null;
      if (poller) clearTimeout(poller);
      poller = null;
    };

    /* ------------------------------------------------------------------ */

    const poll = async () => {
      if (cancelled || !polling) return;

      try {
        const response = await fetch(`${base}?since=${encodeURIComponent(cursor.current ?? '')}`, {
          cache: 'no-store',
        });

        if (!response.ok) {
          // A dead token is 404/410 and there is nothing more to poll for. The
          // page is stale from this moment, so it is reloaded once — the server
          // re-reads the token and renders SCR-WT-006 with no ride data on it.
          if (response.status === 404 || response.status === 410) {
            setClosed(true);
            setConnection('closed');
            stopEverything();
            return;
          }
          throw new Error(String(response.status));
        }

        const batch = (await response.json()) as TrackEventBatch;
        if (cancelled) return;

        if (batch.cursor) cursor.current = batch.cursor;
        batch.events.forEach(apply);
        setConnection((current) => (current === 'closed' ? current : 'polling'));
      } catch {
        if (!cancelled) setConnection('reconnecting');
      }

      if (!cancelled && polling) poller = setTimeout(() => void poll(), POLL_INTERVAL_MS);
    };

    const startPolling = () => {
      stopEverything();
      if (cancelled) return;

      polling = true;
      setConnection('polling');
      void poll();
    };

    /* ------------------------------------------------------------------ */

    const startStreaming = () => {
      if (cancelled) return;

      source = new EventSource(base);

      source.addEventListener('open', () => {
        failedOpens = 0;
        setConnection((current) => (current === 'closed' ? current : 'live'));
      });

      for (const type of ['position', 'status', 'resolved'] as const) {
        source.addEventListener(type, (event) => {
          try {
            apply(JSON.parse((event as MessageEvent<string>).data) as TrackEvent);
          } catch {
            // A frame this page cannot parse is one frame, not a broken feed. The
            // next one carries the same current state — the cursor describes what
            // the client knows, so nothing is lost by dropping this one.
          }
          if (type === 'resolved') stopEverything();
        });
      }

      source.addEventListener('error', () => {
        if (cancelled) return;

        // `CONNECTING` means the browser is already reconnecting with
        // `Last-Event-ID`, which is the whole design. Only a socket the browser
        // has given up on (`CLOSED`) is this hook's problem.
        if (source?.readyState === EventSource.CLOSED) {
          failedOpens += 1;
          if (failedOpens >= STREAM_ATTEMPTS) {
            startPolling();
            return;
          }
          stopEverything();
          startStreaming();
          return;
        }

        setConnection((current) => (current === 'closed' ? current : 'reconnecting'));
      });
    };

    if (typeof EventSource === 'undefined') startPolling();
    else startStreaming();

    return () => {
      cancelled = true;
      stopEverything();
    };
  }, [token, enabled]);

  return { position, status, connection, closed };
}
