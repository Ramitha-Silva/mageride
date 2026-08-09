'use client';

/*
 * MapLibre ships the CSS its canvas container, its controls and its attribution
 * need. That is a widget's functional stylesheet, compiled at build time by the
 * same PostCSS pipeline as everything else — not a pre-styled component kit and
 * not runtime CSS-in-JS, which is what AL-52 excludes and what
 * `scripts/check-bundle.mjs` proves the artefact carries none of. The attribution
 * control is not optional either: OpenStreetMap's licence requires the credit, and
 * this stylesheet is what makes it legible.
 */
import 'maplibre-gl/dist/maplibre-gl.css';

import { useEffect, useRef } from 'react';

import { SEMANTIC_COLORS } from '@mageride/tailwind-preset';
import {
  AttributionControl,
  addProtocol,
  GeoJSONSource,
  Map as MapLibreMap,
  Marker,
  NavigationControl,
  ScaleControl,
} from 'maplibre-gl';
import type { AddLayerObject, MapOptions } from 'maplibre-gl';
// The GeoJSON shapes are imported rather than taken from the global namespace:
// `tsconfig.json` pins `types` to `node`, so nothing is ambient here on purpose.
import type { FeatureCollection } from 'geojson';
import { Protocol } from 'pmtiles';

import type { LngLat } from '@/lib/polyline';

/**
 * The one map on this surface, drawn three ways — SCR-WT-002's driver, SCR-WT-004's
 * driver plus route, and SCR-WT-003's draggable pickup pin.
 *
 * One component and not three, because the three differ in what is on the canvas
 * and in nothing else: same basemap, same controls, same translated UI strings,
 * same "no style URL is a supported state". Three would be three places for the
 * attribution control to go missing.
 *
 * ## It is handed positions and fetches none
 *
 * Every coordinate here came out of `GET /public/track/{token}`, read on the
 * **server** and passed down as props, or off the live feed the page's own
 * `EventSource` is holding. There is no data layer in this component and there
 * must not be one: `src/api/http.ts` is `server-only`, so a client component that
 * imported it would fail to compile.
 *
 * The one URL it fetches is the basemap style: D-14's `tile-cdn`, static
 * cartography on a CDN that carries nobody's ride and takes no credential. It
 * arrives as a **prop** rather than as a build-time public variable, which is what
 * keeps "the browser never learns where the gateway is" true of this surface too.
 *
 * ## The markers are a data-driven layer; only the draggable pin is a DOM node
 *
 * A `Marker` is a positioned `<div>` whose colour would have to be an inline
 * `style`, which AL-52 forbids. Two circle layers over one GeoJSON source put the
 * driver and the endpoints in the WebGL scene with the colour as a **feature
 * property**, and the hexes come from `@mageride/tailwind-preset`'s token data,
 * which exists for exactly this ("so non-CSS consumers — a MapLibre style, a
 * chart, a PDF export — read the same hexes rather than re-typing them").
 *
 * SCR-WT-003's pin is the exception and has to be: dragging is a DOM interaction
 * and MapLibre only offers it on a `Marker`. Its element is built with a Tailwind
 * class string — class arithmetic, not injected CSS — so the fence holds there too.
 *
 * D2's semantic colours have a light and a dark hex; the markers use the **light**
 * pair in both appearances, as D2's own vehicle-marker tokens do. A pin is read
 * against cartography rather than against the page, and cartography does not
 * change with the reader's OS setting.
 */

export interface MapMarker {
  readonly id: string;
  readonly lng: number;
  readonly lat: number;
  readonly kind: 'driver' | 'pickup' | 'dropoff';
}

export interface TrackMapLabels {
  /** The accessible name of the map region and of MapLibre's own canvas. */
  readonly region: string;
  /** Shown over the canvas when the deployment has no basemap. */
  readonly noBasemap: string;
  readonly zoomIn: string;
  readonly zoomOut: string;
  readonly attribution: string;
  readonly metres: string;
  readonly kilometres: string;
  /** SCR-WT-003 only — the pin's accessible name and the "drag to adjust" hint. */
  readonly pin?: string;
  readonly dragHint?: string;
}

export interface TrackMapProps {
  readonly styleUrl: string | null;
  readonly labels: TrackMapLabels;
  readonly markers: readonly MapMarker[];
  /** SCR-WT-004's trip progress line, already decoded. */
  readonly route?: readonly LngLat[];
  /** SCR-WT-003's adjustable pin. Present ⇒ the map is a picker. */
  readonly pin?: { readonly lng: number; readonly lat: number } | undefined;
  readonly onPinMove?: ((point: { lng: number; lat: number }) => void) | undefined;
  /** Tailwind height. The three screens give the map different room. */
  readonly className?: string;
}

const MARKER_COLORS: Record<MapMarker['kind'], string> = {
  driver: SEMANTIC_COLORS.primary.light,
  pickup: SEMANTIC_COLORS.success.light,
  dropoff: SEMANTIC_COLORS.error.light,
};

const POINT_SOURCE = 'track-points';
const ROUTE_SOURCE = 'track-route';
const POINT_LAYER = 'track-points-marker';
const ROUTE_LAYER = 'track-route-line';

/**
 * Sri Lanka, for a map with nothing to draw yet.
 *
 * Fitting an empty set has no answer, and a map that opened on the null island
 * would put the driver in the Gulf of Guinea.
 */
const SRI_LANKA: [number, number, number, number] = [79.5, 5.8, 82.0, 9.9];

/**
 * A style with no sources and no layers, so MapLibre clears the canvas transparent
 * — `painter.render` clears to `Color.transparent` unless a background layer says
 * otherwise — and the container's Tailwind background is what shows through, in
 * either appearance, with no JavaScript reading a theme.
 */
const NO_BASEMAP: NonNullable<MapOptions['style']> = { version: 8, sources: {}, layers: [] };

const ROUTE_LINE: AddLayerObject = {
  id: ROUTE_LAYER,
  source: ROUTE_SOURCE,
  type: 'line',
  layout: { 'line-cap': 'round', 'line-join': 'round' },
  paint: {
    'line-color': SEMANTIC_COLORS.secondary.light,
    'line-width': 4,
    'line-opacity': 0.85,
  },
};

const POINT_CIRCLE: AddLayerObject = {
  id: POINT_LAYER,
  source: POINT_SOURCE,
  type: 'circle',
  paint: {
    'circle-radius': ['case', ['==', ['get', 'kind'], 'driver'], 9, 7],
    'circle-color': ['get', 'color'],
    // The white ring the wireframe draws round every pin. It is what keeps a
    // dark-green endpoint legible over a dark-green field at zoom 15.
    'circle-stroke-width': 2,
    'circle-stroke-color': '#FFFFFF',
  },
};

/**
 * D-14's tiles are PMTiles, so a style whose source is `pmtiles://…` needs the
 * protocol registered before the first tile request. `addProtocol` is global and
 * overwrites, so it is guarded and run once per document.
 */
let protocolRegistered = false;

function registerPmtiles(): void {
  if (protocolRegistered) return;
  protocolRegistered = true;
  addProtocol('pmtiles', new Protocol().tile);
}

function pointsOf(markers: readonly MapMarker[]): FeatureCollection {
  return {
    type: 'FeatureCollection',
    features: markers.map((marker) => ({
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [marker.lng, marker.lat] },
      properties: { kind: marker.kind, color: MARKER_COLORS[marker.kind] },
    })),
  };
}

function routeOf(route: readonly LngLat[]): FeatureCollection {
  return {
    type: 'FeatureCollection',
    features:
      route.length < 2
        ? []
        : [
            {
              type: 'Feature',
              geometry: { type: 'LineString', coordinates: route.map(([lng, lat]) => [lng, lat]) },
              properties: {},
            },
          ],
  };
}

export function TrackMap({
  styleUrl,
  labels,
  markers,
  route = [],
  pin,
  onPinMove,
  className = 'h-[220px]',
}: TrackMapProps) {
  const container = useRef<HTMLDivElement | null>(null);
  const map = useRef<MapLibreMap | null>(null);
  const marker = useRef<Marker | null>(null);
  const framed = useRef(false);

  // MapLibre takes its UI strings once, at construction, and the drag handler has
  // to read the *current* callback. Refs keep both correct without tearing the
  // canvas down when a position arrives — which, on a live feed, is every 2 s.
  const words = useRef(labels);
  const pinMoved = useRef(onPinMove);
  useEffect(() => {
    pinMoved.current = onPinMove;
  }, [onPinMove]);

  useEffect(() => {
    const element = container.current;
    if (!element || map.current) return undefined;

    registerPmtiles();
    const strings = words.current;

    const instance = new MapLibreMap({
      container: element,
      style: styleUrl ?? NO_BASEMAP,
      bounds: SRI_LANKA,
      fitBoundsOptions: { padding: 24 },
      // Mounted below with `compact`, so the credit is present and small rather
      // than absent — the OSM licence is not something a layout may drop.
      attributionControl: false,
      // A tracking page is a glance, not a flyover: pitch and rotation cost the
      // reader their orientation and buy nothing. On a phone they are also two
      // gestures away from being triggered by accident while scrolling.
      pitchWithRotate: false,
      dragRotate: false,
      touchPitch: false,
      locale: {
        'Map.Title': strings.region,
        'NavigationControl.ZoomIn': strings.zoomIn,
        'NavigationControl.ZoomOut': strings.zoomOut,
        'AttributionControl.ToggleAttribution': strings.attribution,
        'ScaleControl.Meters': strings.metres,
        'ScaleControl.Kilometers': strings.kilometres,
      },
    });

    instance.addControl(new AttributionControl({ compact: true }), 'bottom-right');
    instance.addControl(new NavigationControl({ showCompass: false, visualizePitch: false }), 'top-right');
    instance.addControl(new ScaleControl({ unit: 'metric' }), 'bottom-left');

    instance.on('load', () => {
      instance.addSource(ROUTE_SOURCE, { type: 'geojson', data: routeOf([]) });
      instance.addSource(POINT_SOURCE, { type: 'geojson', data: pointsOf([]) });
      // The line is added first so the markers sit on top of it.
      instance.addLayer(ROUTE_LINE);
      instance.addLayer(POINT_CIRCLE);
    });

    map.current = instance;

    return () => {
      marker.current?.remove();
      marker.current = null;
      instance.remove();
      map.current = null;
      framed.current = false;
    };
    // Built once. A changed style URL is a redeployment; everything else is pushed
    // into the sources by the effects below rather than by rebuilding the canvas.
  }, [styleUrl]);

  // The data, and the frame it is first seen in.
  useEffect(() => {
    const instance = map.current;
    if (!instance) return;

    const apply = () => {
      const points = instance.getSource(POINT_SOURCE);
      const line = instance.getSource(ROUTE_SOURCE);
      if (!(points instanceof GeoJSONSource) || !(line instanceof GeoJSONSource)) return;

      points.setData(pointsOf(markers));
      line.setData(routeOf(route));

      // Framed **once**, on the first answer that has anything in it. Re-fitting on
      // every position would yank the view out from under somebody who had zoomed
      // in to see which junction the driver is at — and on this feed a position
      // arrives every two seconds.
      if (framed.current) return;

      const coordinates: LngLat[] = [
        ...markers.map((m) => [m.lng, m.lat] as LngLat),
        ...route,
        ...(pin ? [[pin.lng, pin.lat] as LngLat] : []),
      ];
      if (coordinates.length === 0) return;

      framed.current = true;

      const only = coordinates[0]!;
      if (coordinates.length === 1) {
        // Spread into a fresh tuple: MapLibre's `LngLatLike` is mutable and these
        // are `readonly`, which is the right shape for data that crosses a render.
        instance.jumpTo({ center: [only[0], only[1]], zoom: 15 });
        return;
      }

      const west = Math.min(...coordinates.map(([lng]) => lng));
      const east = Math.max(...coordinates.map(([lng]) => lng));
      const south = Math.min(...coordinates.map(([, lat]) => lat));
      const north = Math.max(...coordinates.map(([, lat]) => lat));

      instance.fitBounds([west, south, east, north], { padding: 48, maxZoom: 15, duration: 0 });
    };

    // `addSource` happens on `load`; before that there is nothing to set data on,
    // and `idle` is the first event after the style and the layers are in place.
    if (instance.getSource(POINT_SOURCE)) apply();
    else instance.once('idle', apply);
  }, [markers, route, pin]);

  // SCR-WT-003's draggable pin.
  useEffect(() => {
    const instance = map.current;
    if (!instance || !pin) return;

    if (!marker.current) {
      const element = document.createElement('div');
      // A class string, not an inline style: the colour is a D2 token utility and
      // the shape is D2's own 4px grid, so AL-52's "no runtime CSS" holds on a node
      // this component creates by hand.
      element.className =
        'size-[26px] rounded-full border-2 border-white bg-success shadow-elevation-2';
      element.setAttribute('role', 'img');
      if (labels.pin) element.setAttribute('aria-label', labels.pin);

      marker.current = new Marker({ element, draggable: true, anchor: 'center' })
        .setLngLat([pin.lng, pin.lat])
        .addTo(instance);

      marker.current.on('dragend', () => {
        const moved = marker.current?.getLngLat();
        if (moved) pinMoved.current?.({ lng: moved.lng, lat: moved.lat });
      });
      return;
    }

    marker.current.setLngLat([pin.lng, pin.lat]);
  }, [pin, labels.pin]);

  // A pin that arrives after the first frame (the reader pressed "use my current
  // location") is worth moving the camera for: they asked to be somewhere else.
  useEffect(() => {
    if (!pin || !map.current || !framed.current) return;
    map.current.easeTo({ center: [pin.lng, pin.lat], duration: 300 });
  }, [pin]);

  return (
    <div
      ref={container}
      role="region"
      aria-label={labels.region}
      className={`relative w-full overflow-hidden rounded-md border border-outline bg-surface-variant ${className}`}
    >
      {styleUrl ? null : (
        <p className="pointer-events-none absolute inset-x-0 bottom-xs z-10 px-sm text-center text-caption text-on-surface-variant">
          {labels.noBasemap}
        </p>
      )}
      {pin && labels.dragHint ? (
        <p className="pointer-events-none absolute inset-x-0 top-xs z-10 px-sm text-center text-caption font-semibold text-on-surface-variant">
          {labels.dragHint}
        </p>
      ) : null}
    </div>
  );
}
