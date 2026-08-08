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
import { usePathname, useRouter, useSearchParams } from 'next/navigation';

import {
  AttributionControl,
  addProtocol,
  GeoJSONSource,
  Map as MapLibreMap,
  NavigationControl,
  ScaleControl,
} from 'maplibre-gl';
import type { AddLayerObject, MapOptions } from 'maplibre-gl';
// The GeoJSON shapes are imported rather than taken from the global namespace:
// `tsconfig.json` pins `types` to `node`, so nothing is ambient here on purpose.
import type { FeatureCollection } from 'geojson';
import { Protocol } from 'pmtiles';

/**
 * **SCR-FP-007's map** — one MapLibre canvas, this organisation's vehicles on it,
 * and nothing else (US-13.3, D2 §FP "Single org-scoped MapLibre map").
 *
 * ## It is handed positions; it never fetches any
 *
 * Every marker here came out of `GET /v1/fleets/{the caller's own id}/map`, read
 * on the **server** by `app/(portal)/map/page.tsx` and passed down as props. There
 * is no client-side data layer and there must not be one: the browser holds no
 * bearer and does not know where the gateway is (`src/api/http.ts` is
 * `server-only`, so a client component importing it fails to compile). "Only this
 * org's vehicles are visible" is therefore not a filter this component applies —
 * it is the only data that ever reaches it, and the database refuses underneath
 * (`telemetry.positions_fleet`, filtered on `app.fleet_id`, fail-closed).
 *
 * The one URL this component fetches is the basemap style: D-14's `tile-cdn`,
 * static cartography on a CDN, carrying nobody's data and taking no bearer.
 *
 * ## Markers are a data-driven layer, not DOM elements
 *
 * A `Marker` per vehicle is a positioned `<div>` per vehicle, which on a
 * 120-vehicle fleet is 120 elements the browser re-lays-out on every pan — and
 * each would need an inline `style` to carry its colour, which AL-52 forbids and
 * `test/fences.test.ts` fails the build on. One GeoJSON source and two circle
 * layers put the whole fleet in the WebGL scene, and the colour, the size and the
 * dimming are **expressions over feature properties** rather than per-element
 * styling.
 *
 * The colours come from `@mageride/tailwind-preset`'s token data, which exists for
 * exactly this ("so non-CSS consumers — a MapLibre style, a chart, a PDF export —
 * read the same hexes rather than re-typing them"). D2's vehicle tokens are one
 * hex in both appearances, so the markers need no theme handling; and with no
 * basemap the canvas is cleared **transparent**, so the container's own
 * `bg-surface-variant` shows through and light/dark stays the page's business.
 *
 * ## Every string MapLibre draws is translated
 *
 * The zoom buttons, the attribution toggle and the scale bar's units are the
 * library's own English defaults. `MapOptions.locale` replaces them, so the map
 * has no more untranslatable copy on it than any other control on this portal
 * (CLAUDE.md, Trilingual resources).
 *
 * ## Selecting a vehicle is a URL, not component state
 *
 * Clicking a marker pushes `?vehicle={id}` — the same state the overlay table's
 * rows link to, and the same idiom C113 used for `?vehicle=` on SCR-FP-004. The
 * drill-in panel is then **server-rendered** from data the page already has, so a
 * deep link into one bus works, the back button works, and the panel cannot
 * disagree with the table beside it.
 */

export interface MapVehicle {
  readonly vehicleId: string;
  readonly lng: number;
  readonly lat: number;
  /** A D2 §0.2 vehicle-type hex, or the private-vehicle grey. */
  readonly color: string;
  /** `false` for a vehicle fleet-health-svc calls stale or offline — drawn faded. */
  readonly live: boolean;
}

export interface FleetMapLabels {
  /** The accessible name of the map region and of MapLibre's own canvas. */
  readonly region: string;
  /** Shown over the canvas when no vehicle reported inside the staleness window. */
  readonly empty: string;
  readonly zoomIn: string;
  readonly zoomOut: string;
  readonly attribution: string;
  readonly metres: string;
  readonly kilometres: string;
}

export interface FleetMapProps {
  readonly vehicles: readonly MapVehicle[];
  readonly selectedVehicleId: string | null;
  /** D-14's style document, or `null` when the deployment has no `tile-cdn`. */
  readonly styleUrl: string | null;
  readonly labels: FleetMapLabels;
}

/**
 * Sri Lanka, for a fleet with nothing to show yet.
 *
 * Fitting an empty set has no answer, and a map that opened on the null island
 * would read as a fleet parked in the Gulf of Guinea. The country is the honest
 * default for a console every one of whose vehicles is in it.
 */
const SRI_LANKA: [number, number, number, number] = [79.5, 5.8, 82.0, 9.9];

const SOURCE = 'fleet-vehicles';
const HALO_LAYER = 'fleet-vehicles-halo';
const MARKER_LAYER = 'fleet-vehicles-marker';

/** Zoomed out far enough that the fleet has plainly not been framed yet. */
const UNFRAMED_ZOOM = 6.2;

/**
 * A style with no sources and no layers, so MapLibre clears the canvas
 * transparent — `painter.render` clears to `Color.transparent` unless a
 * background layer says otherwise — and the container's Tailwind background is
 * what shows through, in either appearance, with no JavaScript reading a theme.
 */
const NO_BASEMAP: NonNullable<MapOptions['style']> = { version: 8, sources: {}, layers: [] };

/**
 * The selection is a **feature property**, not `feature-state`.
 *
 * `setFeatureState` keys on `feature.id`, and a GeoJSON feature id is only
 * dependable when it is an integer — a ULID would go through `geojson-vt` as an
 * id nothing could later address, and the halo would silently never appear. The
 * data is small (one fleet's positions) and is re-set on every navigation
 * anyway, so carrying `selected` in the properties is one fewer moving part and
 * one fewer thing that can be quietly wrong.
 */
const HALO: AddLayerObject = {
  id: HALO_LAYER,
  source: SOURCE,
  type: 'circle',
  paint: {
    'circle-radius': ['case', ['==', ['get', 'selected'], 1], 15, 0],
    'circle-color': ['get', 'color'],
    'circle-opacity': 0.25,
  },
};

const MARKER: AddLayerObject = {
  id: MARKER_LAYER,
  source: SOURCE,
  type: 'circle',
  paint: {
    'circle-radius': ['case', ['==', ['get', 'selected'], 1], 9, 6],
    'circle-color': ['get', 'color'],
    'circle-opacity': ['get', 'opacity'],
    // The white ring the wireframe draws round every pin. It is what keeps a
    // dark-green bus legible over a dark-green field at zoom 15.
    'circle-stroke-width': 2,
    'circle-stroke-color': '#FFFFFF',
    'circle-stroke-opacity': ['get', 'opacity'],
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

function featuresOf(
  vehicles: readonly MapVehicle[],
  selectedVehicleId: string | null,
): FeatureCollection {
  return {
    type: 'FeatureCollection',
    features: vehicles.map((vehicle) => ({
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [vehicle.lng, vehicle.lat] },
      properties: {
        vehicleId: vehicle.vehicleId,
        color: vehicle.color,
        // Numbers rather than booleans: a paint expression interpolates numbers,
        // and "dimmed" is an opacity — the sketch's own faded offline pin.
        opacity: vehicle.live ? 1 : 0.45,
        selected: vehicle.vehicleId === selectedVehicleId ? 1 : 0,
      },
    })),
  };
}

interface Bounds {
  west: number;
  south: number;
  east: number;
  north: number;
}

function boundsOf(vehicles: readonly MapVehicle[]): Bounds | null {
  if (vehicles.length === 0) return null;

  const box: Bounds = { west: 180, south: 90, east: -180, north: -90 };
  for (const vehicle of vehicles) {
    box.west = Math.min(box.west, vehicle.lng);
    box.east = Math.max(box.east, vehicle.lng);
    box.south = Math.min(box.south, vehicle.lat);
    box.north = Math.max(box.north, vehicle.lat);
  }
  return box;
}

export function FleetMap({ vehicles, selectedVehicleId, styleUrl, labels }: FleetMapProps) {
  const container = useRef<HTMLDivElement | null>(null);
  const map = useRef<MapLibreMap | null>(null);

  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  // The click handler needs the current router and the current query string, and
  // the map is built once. A ref keeps the handler reading the latest of both
  // without tearing the canvas down on every navigation.
  const navigate = useRef<(vehicleId: string) => void>(() => {});
  useEffect(() => {
    navigate.current = (vehicleId: string) => {
      const next = new URLSearchParams(searchParams.toString());
      next.set('vehicle', vehicleId);
      router.push(`${pathname}?${next.toString()}`, { scroll: false });
    };
  }, [router, pathname, searchParams]);

  // MapLibre takes its own UI strings once, at construction. The map is built
  // once too, so this ref holds the labels of the render that built it; the page
  // keys this component on the locale, so switching language remounts it rather
  // than leaving English zoom buttons on a Sinhala console.
  const words = useRef(labels);

  useEffect(() => {
    const element = container.current;
    if (!element || map.current) return undefined;

    registerPmtiles();

    const strings = words.current;

    const instance = new MapLibreMap({
      container: element,
      style: styleUrl ?? NO_BASEMAP,
      bounds: SRI_LANKA,
      fitBoundsOptions: { padding: 40 },
      // Mounted below with `compact`, so the credit is present and small rather
      // than absent — the OSM licence is not something a layout may drop.
      attributionControl: false,
      // A fleet console is a monitoring surface, not a flyover: pitch and rotation
      // cost an operator their orientation and buy nothing.
      pitchWithRotate: false,
      dragRotate: false,
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
    instance.addControl(
      new NavigationControl({ showCompass: false, visualizePitch: false }),
      'top-right',
    );
    instance.addControl(new ScaleControl({ unit: 'metric' }), 'bottom-left');

    instance.on('load', () => {
      instance.addSource(SOURCE, { type: 'geojson', data: featuresOf([], null) });
      instance.addLayer(HALO);
      instance.addLayer(MARKER);

      instance.on('click', MARKER_LAYER, (event) => {
        const vehicleId = event.features?.[0]?.properties?.['vehicleId'];
        if (typeof vehicleId === 'string') navigate.current(vehicleId);
      });

      const canvas = instance.getCanvas();
      instance.on('mouseenter', MARKER_LAYER, () => {
        canvas.style.cursor = 'pointer';
      });
      instance.on('mouseleave', MARKER_LAYER, () => {
        canvas.style.cursor = '';
      });
    });

    map.current = instance;

    return () => {
      instance.remove();
      map.current = null;
    };
    // Built once. A changed style URL is a redeployment; the positions are pushed
    // into the source by the effect below rather than by rebuilding the canvas.
  }, [styleUrl]);

  // The positions, the selection and the frame they are all seen in — one effect,
  // because the selection lives in the same feature collection as the positions.
  useEffect(() => {
    const instance = map.current;
    if (!instance) return;

    const apply = () => {
      const source = instance.getSource(SOURCE);
      if (!(source instanceof GeoJSONSource)) return;

      source.setData(featuresOf(vehicles, selectedVehicleId));

      const selected = selectedVehicleId
        ? vehicles.find((vehicle) => vehicle.vehicleId === selectedVehicleId)
        : undefined;

      // A vehicle an operator opened is brought into view, whatever they had
      // been looking at. This is the one case where moving the camera under them
      // is what they asked for.
      if (selected) {
        instance.easeTo({
          center: [selected.lng, selected.lat],
          zoom: Math.max(instance.getZoom(), 13),
          duration: 400,
        });
        return;
      }

      // Otherwise the fleet is framed **once**, on the first answer that has
      // anything in it. Re-fitting on every navigation would yank the view out
      // from under somebody who had zoomed into a junction to watch one bus.
      if (instance.getZoom() > UNFRAMED_ZOOM) return;

      const box = boundsOf(vehicles);
      if (!box) return;

      instance.fitBounds([box.west, box.south, box.east, box.north], {
        padding: 60,
        maxZoom: 14,
        duration: 0,
      });
    };

    // `addSource` happens on `load`; before that there is nothing to set data on,
    // and `idle` is the first event after the style and the layers are in place.
    if (instance.getSource(SOURCE)) apply();
    else instance.once('idle', apply);
  }, [vehicles, selectedVehicleId]);

  return (
    <div
      ref={container}
      role="region"
      aria-label={labels.region}
      className="relative h-[320px] w-full overflow-hidden rounded-md border border-outline bg-surface-variant md:h-[440px]"
    >
      {vehicles.length === 0 ? (
        <p className="pointer-events-none absolute inset-x-0 top-1/2 z-10 -translate-y-1/2 px-md text-center text-body-sm text-on-surface-variant">
          {labels.empty}
        </p>
      ) : null}
    </div>
  );
}
