import { countTone, type AlertRow, type FleetSchedule, type VehicleAnalytics } from '@/api/insights';
import type { FleetHealthRollup } from '@/api/trackers';
import type { FleetVehicle } from '@/api/vehicles';

/**
 * SCR-FP-003's view model — five org-scoped reads turned into four KPI tiles and
 * an alerts feed, on the server, once per render.
 *
 * ## The alerts card is a feed of counts the platform already raised
 *
 * `web_fleet.html` sketches three rows, and this component's fence says **do not
 * build alerting here**. Nothing below watches anything: each row is a count of
 * rows a service has already decided about.
 *
 * | sketch row | what it is here | who decided |
 * |---|---|---|
 * | Vehicle not started (scheduled) | `FleetSchedule.status === 'MISSED'` | fleet-svc's not-started alarm (US-13.11) |
 * | Tracker offline > 15 min | `counts.offline` | fleet-health-svc's silence threshold (US-3.13) |
 * | *(added)* device-down window | `window.alerting` / `alert` | fleet-health-svc's 5-minute breach (US-3.16) |
 * | Insurance expiring (30 d) | **not this row** — see below | — |
 * | *(added)* documents outstanding | `docsStatus === 'docs_pending'` | fleet-svc / the officer's queue (AL-50) |
 *
 * **The insurance-expiry row is the one the platform cannot answer.** Expiry dates
 * live on `VehicleDocumentSlot.expiresAt`, which is only readable per vehicle
 * (`GET …/vehicles/{id}/documents`) — a hundred-vehicle fleet is a hundred
 * requests to draw one number, and no fleet-wide document-expiry route exists on
 * any contract. What one roster read *does* answer is which vehicles still have
 * paperwork outstanding, which is the same operator's next action, so that is the
 * row and the card says what it is instead of the expiry count. Raised in the C114
 * handoff.
 *
 * US-13.5's route-deviation and geofence alerts are Phase 3, have no producer, and
 * `GET …/alerts` answers an empty page "so the Fleet Portal can render an empty
 * state without a later breaking change". The card renders that state as a
 * sentence; it does not fabricate a row.
 */

/* ---------------------------------------------------------------------------
 * The KPI tiles
 * ------------------------------------------------------------------------ */

/** How many vehicles the fleet is actually running — the "of 120" on the sketch. */
export function activeVehicleCount(vehicles: readonly FleetVehicle[]): number {
  return vehicles.filter((vehicle) => vehicle.status === 'APPROVED').length;
}

/**
 * Today's trips, split by mode — `web_fleet.html`'s "842 · Mode A 612 · B 230".
 *
 * The analytics answer has no mode on it, so the split is a join against the
 * roster on `vehicleId`. A vehicle the roster could not be read for (a pending
 * organisation, whose roster is approval-gated) contributes to the total and to
 * neither side of the split, and the tile drops the detail line rather than
 * printing a split that does not add up.
 */
export interface TodaysTrips {
  readonly total: number;
  readonly modeA: number;
  readonly modeB: number;
  /** Whether every counted trip could be attributed to a mode. */
  readonly complete: boolean;
}

export function todaysTrips(
  today: readonly VehicleAnalytics[],
  vehicles: readonly FleetVehicle[],
): TodaysTrips {
  const modes = new Map(vehicles.map((vehicle) => [vehicle.vehicleId, vehicle.mode]));

  let total = 0;
  let modeA = 0;
  let modeB = 0;

  for (const row of today) {
    total += row.tripCount;
    const mode = modes.get(row.vehicleId);
    if (mode === 'A') modeA += row.tripCount;
    else if (mode === 'B') modeB += row.tripCount;
  }

  return { total, modeA, modeB, complete: modeA + modeB === total };
}

/* ---------------------------------------------------------------------------
 * The alerts feed
 * ------------------------------------------------------------------------ */

export interface AlertInputs {
  readonly health: FleetHealthRollup | null;
  readonly schedules: readonly FleetSchedule[];
  readonly vehicles: readonly FleetVehicle[];
  /** False for a pending organisation, whose roster and vehicle screens are gated. */
  readonly rosterReadable: boolean;
}

/**
 * The rows, in the sketch's own order, each linking to the screen an operator
 * would go to about it.
 *
 * A row with a zero count is **kept**, not dropped: "0 trackers offline" is the
 * answer somebody opened the dashboard for, and a card that showed only problems
 * would be indistinguishable from a card whose reads had failed.
 */
export function alertRows(inputs: AlertInputs): AlertRow[] {
  const missed = inputs.schedules.filter((schedule) => schedule.status === 'MISSED').length;
  const offline = inputs.health?.counts.offline ?? 0;
  const stale = inputs.health?.counts.stale ?? 0;
  const outstanding = inputs.vehicles.filter(
    (vehicle) => vehicle.docsStatus === 'docs_pending',
  ).length;

  const rows: AlertRow[] = [
    {
      key: 'not-started',
      labelKey: 'fleet.dashboard.alert.notStarted',
      count: missed,
      tone: countTone(missed, 'warning'),
      href: '/scheduling',
    },
    {
      key: 'offline',
      labelKey: 'fleet.dashboard.alert.trackerOffline',
      count: offline,
      tone: countTone(offline, 'error'),
      href: '/trackers',
    },
    {
      key: 'stale',
      labelKey: 'fleet.dashboard.alert.trackerStale',
      count: stale,
      tone: countTone(stale, 'warning'),
      href: '/trackers',
    },
  ];

  // Only when the roster was readable. A pending organisation cannot list its
  // vehicles at all (`FleetVehiclesGroup` carries `RequireApprovedFleet()`), and
  // "0 vehicles with documents outstanding" over a roster nobody could read is a
  // reassuring number with nothing behind it.
  if (inputs.rosterReadable) {
    rows.push({
      key: 'documents',
      labelKey: 'fleet.dashboard.alert.documentsOutstanding',
      count: outstanding,
      tone: countTone(outstanding, 'warning'),
      href: '/vehicles',
    });
  }

  return rows;
}

/**
 * US-3.16's device-down breach, when the most recent closed five-minute bucket is
 * over `Health:OfflinePct`.
 *
 * `window.alerting` is this bucket; `alert` is the last one that fired, which may
 * be hours old. The banner is drawn for the **current** window only — an alert
 * from Tuesday over a fleet that is fine now is history, and the place for it is
 * not the top of a live dashboard.
 */
export function deviceDownWindow(health: FleetHealthRollup | null) {
  const window = health?.window;
  return window?.alerting ? window : null;
}
