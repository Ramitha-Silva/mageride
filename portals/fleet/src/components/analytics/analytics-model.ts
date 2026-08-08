import {
  analyticsTotals,
  idleHours,
  type AnalyticsRange,
  type AnalyticsTotals,
  type VehicleAnalytics,
} from '@/api/insights';
import type { FleetVehicle } from '@/api/vehicles';
import type { FleetTranslator, Locale } from '@/i18n';

import { vehicleTypeLabel } from '@/components/vehicles/vehicle-model';
import { vehicleAccentClass } from '@/api/vehicles';

/**
 * SCR-FP-009's view model — one analytics answer turned into the wireframe's four
 * tiles and five columns, on the server, once per render.
 *
 * ## Every number here is fleet-svc's, or a subtraction from two of them
 *
 * `tripCount`, `distanceKm`, `activeHours` and `utilisationPct` are read straight
 * off `GET …/analytics`, which computes them from `trips.sessions_fleet` and the
 * `telemetry.positions_fleet` hypertable under row-level security. **Idle is the
 * one derived figure** and is the complement of the service's own utilisation
 * definition rather than a second measurement — see `idleHours` in `@/api/insights`
 * for why it is a subtraction and what it therefore does and does not mean.
 *
 * ## Distance is great-circle, and the caption says so
 *
 * `FleetInsightsRepository` sums great-circle hops between consecutive telemetry
 * samples: "this is **not road distance** — nothing in this build map-matches a
 * completed journey … It is therefore an under-estimate on a winding road and an
 * over-estimate on a jittery fix, and it is what US-13.4's 'distance' can honestly
 * mean today" (raised in the C059 handoff). An operator reconciling a fuel claim
 * against this column needs to know that, so the screen says it rather than
 * leaving the number to look like an odometer reading.
 */

export interface AnalyticsRow {
  readonly vehicleId: string;
  readonly plate: string;
  readonly type: string | null;
  readonly accentClass: string;
  readonly trips: string;
  readonly distance: string;
  readonly utilisation: string;
  readonly idle: string;
}

export function analyticsRow(
  item: VehicleAnalytics,
  range: AnalyticsRange,
  roster: ReadonlyMap<string, FleetVehicle>,
  locale: Locale,
  t: FleetTranslator,
): AnalyticsRow {
  const vehicle = roster.get(item.vehicleId);

  return {
    vehicleId: item.vehicleId,
    plate: item.registrationNumber ?? vehicle?.registrationNumber ?? item.vehicleId,
    type: vehicle
      ? t('fleet.vehicles.typeWithMode', {
          type: vehicleTypeLabel(vehicle.vehicleType, t),
          mode: vehicle.mode,
        })
      : null,
    accentClass: vehicleAccentClass(vehicle?.vehicleType ?? ''),
    trips: formatNumber(locale, item.tripCount, 0),
    distance: t('fleet.analytics.km', { distance: formatNumber(locale, item.distanceKm, 0) }),
    utilisation: t('fleet.analytics.percent', {
      percent: formatNumber(locale, item.utilisationPct ?? 0, 0),
    }),
    idle: t('fleet.analytics.hours', {
      hours: formatNumber(locale, idleHours(item, range), 1),
    }),
  };
}

/** SCR-FP-009's four tiles, already worded. */
export interface AnalyticsSummary {
  readonly totals: AnalyticsTotals;
  readonly trips: string;
  readonly distance: string;
  readonly utilisation: string;
  readonly idlePerDay: string;
}

export function analyticsSummary(
  items: readonly VehicleAnalytics[],
  range: AnalyticsRange,
  locale: Locale,
  t: FleetTranslator,
): AnalyticsSummary {
  const totals = analyticsTotals(items, range);

  return {
    totals,
    trips: formatNumber(locale, totals.trips, 0),
    distance: t('fleet.analytics.km', {
      distance: formatNumber(locale, totals.distanceKm, 0),
    }),
    utilisation: t('fleet.analytics.percent', {
      percent: formatNumber(locale, totals.utilisationPct, 0),
    }),
    idlePerDay: t('fleet.analytics.hours', {
      hours: formatNumber(locale, totals.idleHoursPerDay, 1),
    }),
  };
}

/**
 * The CSV's rows: a header, then one line per vehicle, in the table's own order.
 *
 * **Unworded on purpose.** The header is translated — it is a heading somebody
 * reads — but the cells are raw numbers with a `.` decimal separator and no
 * grouping, because a CSV is opened in a spreadsheet, and a grouped `84,210`
 * lands in the next column while a Sinhala-formatted numeral does not parse as a
 * number at all. The formatted, localised numbers are what the screen shows; this
 * is what a spreadsheet can add up.
 */
export function analyticsCsv(
  items: readonly VehicleAnalytics[],
  range: AnalyticsRange,
  roster: ReadonlyMap<string, FleetVehicle>,
  t: FleetTranslator,
): (string | number)[][] {
  const header = [
    t('fleet.analytics.csv.vehicleId'),
    t('fleet.analytics.column.vehicle'),
    t('fleet.analytics.csv.vehicleType'),
    t('fleet.analytics.csv.mode'),
    t('fleet.analytics.column.trips'),
    t('fleet.analytics.csv.distanceKm'),
    t('fleet.analytics.csv.activeHours'),
    t('fleet.analytics.csv.utilisationPct'),
    t('fleet.analytics.csv.idleHours'),
  ];

  const rows = items.map((item) => {
    const vehicle = roster.get(item.vehicleId);
    return [
      item.vehicleId,
      item.registrationNumber ?? vehicle?.registrationNumber ?? '',
      vehicle?.vehicleType ?? '',
      vehicle?.mode ?? '',
      item.tripCount,
      round(item.distanceKm, 3),
      round(item.activeHours ?? 0, 3),
      round(item.utilisationPct ?? 0, 2),
      round(idleHours(item, range), 3),
    ];
  });

  return [header, ...rows];
}

function round(value: number, places: number): number {
  const factor = 10 ** places;
  return Math.round(value * factor) / factor;
}

/**
 * A number in the operator's own locale — grouped, and with at most `places`
 * decimals.
 *
 * Not `@/i18n/format`'s money formatter: these are counts, kilometres and hours,
 * not minor units, and none of them crosses the money boundary.
 */
function formatNumber(locale: Locale, value: number, places: number): string {
  return new Intl.NumberFormat(`${locale}-LK`, {
    minimumFractionDigits: 0,
    maximumFractionDigits: places,
  }).format(value);
}
