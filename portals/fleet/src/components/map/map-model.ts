import { VEHICLE_COLORS } from '@mageride/tailwind-preset';
import type { StatusTone } from '@mageride/ui';

import type { Assignment } from '@/api/drivers';
import { speedKmh, type FleetVehiclePosition } from '@/api/insights';
import { trackerStateView, type TrackerHealth } from '@/api/trackers';
import { vehicleAccentClass, type FleetVehicle } from '@/api/vehicles';
import { formatSince } from '@/i18n/format';
import type { FleetTranslator, Locale } from '@/i18n';

import { vehicleTypeLabel } from '@/components/vehicles/vehicle-model';

/**
 * SCR-FP-007's view model — four org-scoped reads turned into the pins on the map
 * and the rows of the fleet-health overlay, on the server, once per render.
 *
 * | what | route | owner |
 * |---|---|---|
 * | where each vehicle is | `GET …/map` | fleet-svc |
 * | how each tracker is | `GET …/health` | fleet-health-svc |
 * | what each vehicle is called | `GET …/vehicles` | fleet-svc |
 * | who is driving it | `GET …/assignments` | fleet-svc |
 *
 * The first is the screen; the other three are labels on it, and the page treats
 * them that way — the map read failing is the screen failing, the rest failing
 * costs a column.
 *
 * ## Two windows, and the overlay is the one that shows both
 *
 * fleet-svc drops a position older than `Fleet:MapStaleAfter` (15 min) from the
 * map answer entirely: "a vehicle whose last sample is older than the window has
 * no position, and drawing it there is worse than leaving it off". fleet-health-svc
 * calls a tracker `offline` after `Health:OfflineAfter` (30 min). **The two do not
 * line up, and they are not meant to** — one is about a stale coordinate and the
 * other about a silent device.
 *
 * So the overlay is built over the **union** rather than over the map answer: a
 * vehicle that went quiet twenty minutes ago is Offline in the table with no pin
 * on the map, which is the true state, where a table built from the pins would
 * quietly lose it and a map built from the health list would draw it at the last
 * place it was ever seen. The screen's caption says both windows.
 */

/* ---------------------------------------------------------------------------
 * Colour
 * ------------------------------------------------------------------------ */

/**
 * The D2 §0.2 vehicle-type marker hex (MAP-03's legend), for a WebGL paint
 * expression that cannot read a CSS custom property.
 *
 * **Built from the token data, keyed on the token's own `vehicleType`.** The
 * preset publishes the tokens "so non-CSS consumers — a MapLibre style, a chart, a
 * PDF export — read the same hexes rather than re-typing them", and each carries
 * the canonical `registry.vehicles.vehicle_type` it belongs to; deriving the map
 * from that field means a colour cannot drift from {@link vehicleAccentClass}'s,
 * which is the same legend rendered as a Tailwind utility.
 *
 * `veh-private` is the fallback and is the one token with no vehicle type — "a
 * Mode B *display* token, not a vehicle type" — which is exactly the right answer
 * for a type this build has not heard of.
 */
const ACCENT_HEXES: Readonly<Record<string, string>> = Object.fromEntries(
  Object.values(VEHICLE_COLORS)
    .filter((token) => token.vehicleType !== null)
    .map((token) => [token.vehicleType as string, token.hex]),
);

const PRIVATE_HEX = VEHICLE_COLORS['veh-private'].hex;

export function vehicleAccentHex(vehicleType: string | undefined): string {
  return (vehicleType && ACCENT_HEXES[vehicleType]) || PRIVATE_HEX;
}

/* ---------------------------------------------------------------------------
 * The union the screen is built over
 * ------------------------------------------------------------------------ */

/** Everything the screen knows about one vehicle, from whichever reads answered. */
export interface FleetMapVehicle {
  readonly vehicleId: string;
  readonly position: FleetVehiclePosition | null;
  readonly health: TrackerHealth | null;
  readonly vehicle: FleetVehicle | null;
  readonly driver: Assignment | null;
}

export interface FleetMapInputs {
  readonly positions: readonly FleetVehiclePosition[];
  readonly health: readonly TrackerHealth[];
  readonly vehicles: readonly FleetVehicle[];
  readonly assignments: readonly Assignment[];
}

/**
 * The union, in plate order.
 *
 * Ordered by plate rather than by health, because an operator scanning for
 * "NB-4521" is reading the column they know the value of. The counters above the
 * map are what says how many are in trouble.
 */
export function fleetMapVehicles(inputs: FleetMapInputs): FleetMapVehicle[] {
  const positions = new Map(inputs.positions.map((row) => [row.vehicleId, row]));
  const health = new Map(inputs.health.map((row) => [row.vehicleId, row]));
  const vehicles = new Map(inputs.vehicles.map((row) => [row.vehicleId, row]));

  // Active first, so a vehicle with a revoked assignment and a current one shows
  // whoever is actually driving it. `active` is the database's answer evaluated at
  // read time and is never recomputed here (`@/api/drivers`).
  const drivers = new Map<string, Assignment>();
  for (const assignment of inputs.assignments) {
    if (!assignment.active) continue;
    if (!drivers.has(assignment.vehicleId)) drivers.set(assignment.vehicleId, assignment);
  }

  const ids = new Set<string>([...positions.keys(), ...health.keys(), ...vehicles.keys()]);

  return [...ids]
    .map((vehicleId) => ({
      vehicleId,
      position: positions.get(vehicleId) ?? null,
      health: health.get(vehicleId) ?? null,
      vehicle: vehicles.get(vehicleId) ?? null,
      driver: drivers.get(vehicleId) ?? null,
    }))
    .sort((a, b) => plateOf(a).localeCompare(plateOf(b)));
}

function plateOf(row: FleetMapVehicle): string {
  return row.vehicle?.registrationNumber ?? row.position?.registrationNumber ?? row.vehicleId;
}

/**
 * Whether this vehicle's marker is drawn at full strength.
 *
 * "Offline markers dimmed" is the wireframe's own state. `stale` is dimmed too:
 * the pin is a position the platform is no longer confident about, and drawing it
 * as solid as a bus reporting every four seconds is the map claiming more than it
 * knows. A vehicle with **no** health row — the fleet is larger than
 * `Health:MaxItems`, or its position is mobile-sourced rather than from a bound
 * tracker — is drawn solid: it reported inside the map window, which is the only
 * thing the marker asserts.
 */
export function isLive(row: FleetMapVehicle): boolean {
  return row.health === null || row.health.state === 'online';
}

/* ---------------------------------------------------------------------------
 * The overlay table
 * ------------------------------------------------------------------------ */

export interface OverlayRow {
  readonly vehicleId: string;
  readonly plate: string;
  /** `Bus (A)` — the type and mode, as the roster table prints it. */
  readonly type: string | null;
  readonly accentClass: string;
  readonly driver: string;
  readonly speed: string;
  readonly battery: string;
  readonly health: string;
  readonly healthTone: StatusTone;
  readonly selected: boolean;
}

/**
 * One overlay row. Takes no locale: every cell is either a plate, a translated
 * word or a whole number — "how long ago" belongs on the drill-in panel, where
 * there is room to say it, rather than in a column the sketch does not draw.
 */
export function overlayRow(
  row: FleetMapVehicle,
  selectedVehicleId: string | null,
  t: FleetTranslator,
): OverlayRow {
  const state = row.health ? trackerStateView(row.health.state) : null;
  const kmh = speedKmh(row.position?.speedMps);

  return {
    vehicleId: row.vehicleId,
    plate: plateOf(row),
    type: row.vehicle
      ? t('fleet.vehicles.typeWithMode', {
          type: vehicleTypeLabel(row.vehicle.vehicleType, t),
          mode: row.vehicle.mode,
        })
      : null,
    accentClass: vehicleAccentClass(row.vehicle?.vehicleType ?? ''),
    driver: row.driver?.driverName ?? row.driver?.driverPhone ?? t('fleet.map.noDriver'),
    speed: kmh === null ? '—' : t('fleet.map.speedKmh', { speed: kmh }),
    battery: batteryCell(row.health, t),
    health: state ? t(state.labelKey) : t('fleet.map.noTracker'),
    healthTone: state ? state.tone : 'neutral',
    selected: row.vehicleId === selectedVehicleId,
  };
}

/**
 * Battery, in whichever unit the device actually reported.
 *
 * "Most of the population does not [report a percentage]: a GT06 status byte
 * carries a coarse voltage level and JT/T 808 additional items carry millivolts,
 * and neither converts to a percentage without knowing the pack"
 * (`fleet-health.yaml`). So millivolts are shown as millivolts rather than divided
 * by a number nobody has — the sketch's own "—" is what a device that reported
 * neither gets.
 */
function batteryCell(health: TrackerHealth | null, t: FleetTranslator): string {
  if (health?.battery !== undefined) return t('fleet.map.batteryPct', { percent: health.battery });
  if (health?.batteryMv !== undefined) return t('fleet.map.batteryMv', { mv: health.batteryMv });
  return '—';
}

/* ---------------------------------------------------------------------------
 * The drill-in
 * ------------------------------------------------------------------------ */

/** One labelled fact on the per-vehicle panel. */
export interface DetailFact {
  readonly key: string;
  readonly label: string;
  readonly value: string;
}

/**
 * The per-vehicle drill-in: everything four reads know about one bus, and an
 * explicit sentence for each thing they do not.
 *
 * Deliberately flat labelled facts rather than a second table. The panel answers
 * "what is this vehicle doing", and the reason a field is empty ("no tracker is
 * bound", "no position inside the window") is as much of an answer as a number.
 */
export function detailFacts(
  row: FleetMapVehicle,
  locale: Locale,
  t: FleetTranslator,
): DetailFact[] {
  const kmh = speedKmh(row.position?.speedMps);
  const heading = row.position?.heading;

  const facts: DetailFact[] = [
    {
      key: 'driver',
      label: t('fleet.map.column.driver'),
      value: row.driver?.driverName ?? row.driver?.driverPhone ?? t('fleet.map.noDriver'),
    },
    {
      key: 'speed',
      label: t('fleet.map.column.speed'),
      value: kmh === null ? t('fleet.map.noPosition') : t('fleet.map.speedKmh', { speed: kmh }),
    },
    {
      key: 'heading',
      label: t('fleet.map.heading'),
      value:
        heading === undefined
          ? t('fleet.map.noHeading')
          : t('fleet.map.headingDegrees', { degrees: heading, compass: compassPoint(heading, t) }),
    },
    {
      key: 'sample',
      label: t('fleet.map.lastSample'),
      value: formatSince(locale, row.position?.sampleTs) ?? t('fleet.map.noPosition'),
    },
    {
      key: 'health',
      label: t('fleet.map.column.health'),
      value: row.health
        ? t(trackerStateView(row.health.state).labelKey)
        : t('fleet.map.noTracker'),
    },
    { key: 'battery', label: t('fleet.map.column.battery'), value: batteryCell(row.health, t) },
  ];

  if (row.health?.signalStrength !== undefined) {
    facts.push({
      key: 'signal',
      label: t('fleet.map.signal'),
      value: String(row.health.signalStrength),
    });
  }

  if (row.health?.sats !== undefined) {
    facts.push({ key: 'sats', label: t('fleet.map.satellites'), value: String(row.health.sats) });
  }

  return facts;
}

/**
 * The eight-point compass name for a heading, beside the degrees.
 *
 * `heading` is degrees clockwise from north, and "214°" alone is a number a
 * dispatcher has to convert in their head while looking at a map. The word is a
 * resource string, so it is a word in Sinhala and Tamil too.
 */
const COMPASS_KEYS = [
  'fleet.map.compass.n',
  'fleet.map.compass.ne',
  'fleet.map.compass.e',
  'fleet.map.compass.se',
  'fleet.map.compass.s',
  'fleet.map.compass.sw',
  'fleet.map.compass.w',
  'fleet.map.compass.nw',
] as const;

export function compassPoint(heading: number, t: FleetTranslator): string {
  const index = Math.round((((heading % 360) + 360) % 360) / 45) % COMPASS_KEYS.length;
  return t(COMPASS_KEYS[index] ?? 'fleet.map.compass.n');
}
