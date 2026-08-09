'use client';

import dynamic from 'next/dynamic';

import type { TrackMapProps } from './TrackMap';

/**
 * `TrackMap`, behind a lazy boundary.
 *
 * MapLibre is by far the largest thing this application ships — more than the rest
 * of the page put together — and three of the six screens never draw a map at all.
 * Two of those three (**SCR-WT-005** and, when the parcel has arrived, the receipt
 * view of the same route) share a route file with a screen that does, so without a
 * boundary here Next would send the whole renderer to a phone that is looking at a
 * "Package delivered" tick.
 *
 * `React.lazy` under the hood, so the chunk is requested **when the component
 * actually renders** rather than when its route is entered. SSR is left on: the
 * component's own markup is a sized container, so the server still emits the box
 * the canvas will fill and nothing shifts under the reader's thumb when the chunk
 * lands.
 *
 * The placeholder is the same box in the same colour, and carries no text: a
 * "loading map" caption that appears for 300 ms and then vanishes is noise, and it
 * would be the one string on this surface with nothing to say.
 */
export const LazyTrackMap = dynamic<TrackMapProps>(
  () => import('./TrackMap').then((module) => module.TrackMap),
  {
    loading: () => (
      <div
        aria-hidden="true"
        className="h-[200px] w-full animate-pulse rounded-md border border-outline bg-surface-variant"
      />
    ),
  },
);
