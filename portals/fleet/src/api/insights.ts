import type { StatusTone } from '@mageride/ui';

import type { FleetMessageKey } from '@/i18n';

/**
 * **SCR-FP-003, SCR-FP-007 and SCR-FP-009** — the dashboard, the live map and the
 * trip/analytics report (US-13.3, US-13.4, T-06, T-10), transcribed from
 * `backend/contracts/fleet.yaml` and from the constants `Fleet.Api` gates them on.
 *
 * Nothing here calls anything. The org-relative targets are the strings a screen
 * hands `read()`, which is where the caller's own `fleetId` is written into a URL
 * and the only place it ever is (`./client`).
 *
 * ## Why "row-level-security scoped" needs nothing from this side
 *
 * Both reads go through views that are filtered in the **database**, not in
 * fleet-svc: `GET …/map` reads `telemetry.positions_fleet` and the analytics
 * `telemetry.positions_fleet` + `trips.sessions_fleet`, each behind
 * `SET LOCAL ROLE mageride_fleet_reader` with `app.fleet_id` set — "fail-closed:
 * an unscoped connection sees zero rows rather than an error a caller might retry
 * unscoped" (`fleet.yaml`, C006). The portal's half of the fence is that a screen
 * cannot name an organisation at all, which `./client` enforces and
 * `test/fences.test.ts` asserts over the tree. Neither half is trusted alone.
 *
 * ## Three numbers this file carries that no response does
 *
 * `MAP_STALE_AFTER_SECONDS`, `MAX_ANALYTICS_DAYS` and `DEFAULT_ANALYTICS_DAYS`
 * are `FleetOptions.MapStaleAfter`, `.MaxAnalyticsDays` and the analytics
 * service's own default window. They are **not** on the wire, and they change
 * what an operator is looking at, so a screen that did not say them would print
 * "12 vehicles" over a map that had silently dropped everything quiet for a
 * quarter of an hour. `test/insights.test.ts` pins all three against the C# so a
 * retuned deployment shows up as a failing test rather than as a wrong caption.
 */

/* ---------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------ */

/** `GET /v1/fleets/{own id}/map` — one row per vehicle, newest sample (US-13.3). */
export const FLEET_MAP = '/map';

/** `GET /v1/fleets/{own id}/analytics` — per-vehicle trips, distance, hours (US-13.4). */
export const FLEET_ANALYTICS = '/analytics';

/** `GET /v1/fleets/{own id}/alerts` — US-13.5's page, **empty in Phase 1**. */
export const FLEET_ALERTS = '/alerts';

/** `GET /v1/fleets/{own id}/schedules` — departures, soonest first (US-13.11). */
export const FLEET_SCHEDULES = '/schedules';

/* ---------------------------------------------------------------------------
 * The live map — fleet.yaml#/components/schemas/FleetVehiclePosition
 * ------------------------------------------------------------------------ */

export interface FleetVehiclePosition {
  readonly vehicleId: string;
  readonly registrationNumber?: string;
  readonly lat: number;
  readonly lng: number;
  /** Degrees clockwise from north, 0–359. Absent when the source reported none. */
  readonly heading?: number;
  readonly speedMps?: number;
  /** The **sample** instant, which is the GNSS capture and not the receive time. */
  readonly sampleTs: string;
}

export interface FleetMapAnswer {
  readonly vehicles: readonly FleetVehiclePosition[];
  readonly asOf: string;
}

/**
 * `FleetOptions.MapStaleAfter` — fleet-svc drops a sample older than this from
 * the map entirely (`ReadMapAsync`: "a vehicle whose last sample is older than
 * the window has no position, and drawing it there is worse than leaving it off").
 *
 * So "the map is missing a vehicle" and "the vehicle is offline" are the same
 * fact seen from two services, and the screen says the window rather than leaving
 * an operator to count rows against a roster.
 */
export const MAP_STALE_AFTER_SECONDS = 15 * 60;

/** m/s on the wire (`speedMps`); every speedometer in Sri Lanka reads km/h. */
export function speedKmh(speedMps: number | undefined): number | null {
  if (speedMps === undefined || !Number.isFinite(speedMps) || speedMps < 0) return null;
  return Math.round(speedMps * 3.6);
}

/* ---------------------------------------------------------------------------
 * Analytics — fleet.yaml#/components/schemas/VehicleAnalytics
 * ------------------------------------------------------------------------ */

export interface VehicleAnalytics {
  readonly vehicleId: string;
  readonly registrationNumber?: string;
  readonly tripCount: number;
  readonly distanceKm: number;
  readonly activeHours?: number;
  readonly utilisationPct?: number;
  /**
   * **Always absent, and that is the answer rather than a gap.** A fleet's
   * Mode A/B vehicles take no fares on this platform — a bus fare is collected on
   * the bus and a Mode B subscription is a pass-through to the operator's own bank
   * account MageRide never sees (BR-23.10) — so `FleetInsightsRepository` returns
   * `null` deliberately: "zero would be a claim that the operator earned nothing;
   * absent is the truth, which is that this platform does not know". The report
   * therefore has no earnings column, rather than a column of dashes.
   */
  readonly earningsMinor?: number;
  readonly currency?: string;
}

export interface FleetAnalyticsAnswer {
  readonly items: readonly VehicleAnalytics[];
}

/** `FleetOptions.MaxAnalyticsDays` — the widest range one request may report. */
export const MAX_ANALYTICS_DAYS = 92;

/** `FleetInsightsService.ReadAnalyticsAsync`'s own default: the last thirty days. */
export const DEFAULT_ANALYTICS_DAYS = 30;

/**
 * The period an operator is looking at, as two `BusinessDate`s.
 *
 * Both ends are **inclusive Colombo days** — "`to` is inclusive of the day named"
 * (`FleetInsightsService`) — so `2026-06-01` to `2026-06-17` is seventeen days,
 * not sixteen.
 */
export interface AnalyticsRange {
  readonly from: string;
  readonly to: string;
  readonly days: number;
}

const BUSINESS_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Whether a value is a `_shared.yaml#/BusinessDate` this screen can send. */
export function isBusinessDate(value: string | undefined | null): value is string {
  if (!value || !BUSINESS_DATE.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}

/**
 * Today in **Asia/Colombo**, as a `BusinessDate`.
 *
 * Not the process's today. A Next container runs in UTC and the operator is in
 * Sri Lanka, so between 18:30 and midnight UTC the two disagree about which day
 * it is — and "today's trips" on the dashboard would be yesterday's for the five
 * and a half hours of the evening an operator is most likely to be looking.
 *
 * This picks a **request parameter**, never a server fact: fleet-svc evaluates the
 * period against its own clock through `BusinessCalendar` (D-13), and this only
 * decides which period the screen asks about.
 */
export function colomboToday(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Colombo',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(now);
}

/** `date` shifted by whole days. Days are calendar days here, never 86 400 s. */
export function shiftBusinessDate(date: string, days: number): string {
  const instant = new Date(`${date}T00:00:00Z`);
  instant.setUTCDate(instant.getUTCDate() + days);
  return instant.toISOString().slice(0, 10);
}

/** How many inclusive days `from`…`to` spans. Zero or less means the range is inverted. */
export function daysBetween(from: string, to: string): number {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(start) || Number.isNaN(end)) return 0;
  return Math.round((end - start) / 86_400_000) + 1;
}

/** What went wrong with a range an operator typed, or `null` when nothing did. */
export type RangeProblem = 'inverted' | 'too-long' | null;

export function rangeProblem(from: string, to: string): RangeProblem {
  const days = daysBetween(from, to);
  if (days < 1) return 'inverted';
  if (days > MAX_ANALYTICS_DAYS) return 'too-long';
  return null;
}

/**
 * The range the screen will actually report, from whatever was in the query
 * string.
 *
 * **The defaults are fleet-svc's own, restated rather than left to the server.**
 * The service defaults an absent range to the last thirty Colombo days; the
 * screen sends them explicitly because the *caption* has to name the period the
 * figures cover, and a caption that said "last 30 days" while the service had
 * quietly changed its default would be the one line on the page nobody could
 * check. `test/insights.test.ts` holds the two together against the C#.
 *
 * A malformed or inverted pair falls back to the default window rather than
 * being sent: `from > to` is `400 validation-failed`, and answering a typo with
 * an error page instead of a report is a worse screen.
 */
export function analyticsRange(
  params: { readonly from?: string; readonly to?: string },
  today: string = colomboToday(),
): AnalyticsRange {
  const to = isBusinessDate(params.to) ? params.to : today;
  const requested = isBusinessDate(params.from) ? params.from : null;
  const from =
    requested && rangeProblem(requested, to) === null
      ? requested
      : shiftBusinessDate(to, -(DEFAULT_ANALYTICS_DAYS - 1));

  return { from, to, days: daysBetween(from, to) };
}

/** The single Colombo day `date` — what "today's trips" asks fleet-svc for. */
export function singleDay(date: string): AnalyticsRange {
  return { from: date, to: date, days: 1 };
}

/* ---------------------------------------------------------------------------
 * Idle, and why it is a subtraction rather than a field
 * ------------------------------------------------------------------------ */

/**
 * Hours a vehicle spent **outside a journey** over the period.
 *
 * SCR-FP-009 asks for idle time and `VehicleAnalytics` has no field for it. It is
 * not missing: fleet-svc computes `utilisationPct` as
 * `activeHours × 100 / periodHours`, so idle is that definition's complement over
 * the same period, and deriving it here uses the server's own arithmetic rather
 * than inventing a second one.
 *
 * **It is calendar time, not engine-off time.** Nothing on the platform measures a
 * stationary running engine — telemetry is positions, and `trips.sessions_fleet`
 * is journeys — so a bus parked overnight is idle for those hours. The screen's
 * caption says exactly that; a column labelled "Idle" with no such sentence would
 * read as the depot metric it is not.
 */
export function idleHours(row: VehicleAnalytics, range: AnalyticsRange): number {
  const period = range.days * 24;
  return Math.max(0, period - (row.activeHours ?? 0));
}

/** SCR-FP-009's four KPI tiles, over every vehicle in the answer. */
export interface AnalyticsTotals {
  readonly vehicles: number;
  readonly trips: number;
  readonly distanceKm: number;
  /** Fleet-wide: Σ active hours over Σ available hours, not a mean of the column. */
  readonly utilisationPct: number;
  /** Mean idle hours per vehicle per day. */
  readonly idleHoursPerDay: number;
}

export function analyticsTotals(
  items: readonly VehicleAnalytics[],
  range: AnalyticsRange,
): AnalyticsTotals {
  const trips = items.reduce((sum, row) => sum + row.tripCount, 0);
  const distanceKm = items.reduce((sum, row) => sum + row.distanceKm, 0);
  const activeHours = items.reduce((sum, row) => sum + (row.activeHours ?? 0), 0);

  // Σ active over Σ available, never the mean of `utilisationPct`. A fleet of one
  // busy minibus and forty parked coaches is not 50% utilised, and averaging the
  // per-vehicle percentages is how it would come out that way.
  const available = items.length * range.days * 24;
  const idle = items.reduce((sum, row) => sum + idleHours(row, range), 0);

  return {
    vehicles: items.length,
    trips,
    distanceKm,
    utilisationPct: available > 0 ? Math.min(100, (activeHours * 100) / available) : 0,
    idleHoursPerDay: items.length > 0 && range.days > 0 ? idle / items.length / range.days : 0,
  };
}

/* ---------------------------------------------------------------------------
 * The CSV export
 * ------------------------------------------------------------------------ */

/**
 * `web_fleet.html` draws "Export CSV / PDF" on SCR-FP-009 and **no contract has an
 * analytics export route** — `exportFleetInvoice` is fleet-billing-svc's and
 * produces one month's invoice, which is a different document about different
 * numbers.
 *
 * So the CSV is built from the answer the screen already read, by
 * `app/(portal)/analytics/export/route.ts`: no second read, no figure that is not
 * on the page, and the same org-scoped call behind the same gate. The PDF is the
 * browser's own print renderer over the same rows (see `ExportControls`).
 *
 * `\r\n` and a UTF-8 BOM because the file is opened in Excel more often than not,
 * and Excel reads a BOM-less UTF-8 CSV as Windows-1252 — which turns a Sinhala
 * plate note into mojibake in the one program every operator has.
 */
export const CSV_BOM = '\uFEFF';

export function csvCell(value: string | number): string {
  const text = String(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export function csvRows(rows: readonly (readonly (string | number)[])[]): string {
  return CSV_BOM + rows.map((row) => row.map(csvCell).join(',')).join('\r\n') + '\r\n';
}

/** `mageride-fleet-analytics-2026-06-01-to-2026-06-17.csv` — the period is in the name, not only in the file. */
export function analyticsFileName(range: AnalyticsRange): string {
  return `mageride-fleet-analytics-${range.from}-to-${range.to}.csv`;
}

/* ---------------------------------------------------------------------------
 * Alerts — fleet.yaml#/components/schemas/FleetAlert
 * ------------------------------------------------------------------------ */

/**
 * `FleetAlert.kind`. **Nothing on this platform emits one.** US-13.5's
 * route-deviation and geofence alerting is Phase 3 and has no producer;
 * `ListAlertsAsync` returns an empty page by construction, and the contract says
 * the route exists now "so the Fleet Portal can render an empty state without a
 * later breaking change".
 *
 * The dashboard therefore reads it and renders that empty state, and does not
 * build alerting — which is this component's own fence, in as many words.
 */
export type FleetAlertKind = 'route_deviation' | 'geofence_enter' | 'geofence_exit';

export interface FleetAlert {
  readonly alertId: string;
  readonly kind: FleetAlertKind;
  readonly vehicleId: string;
  readonly geofenceName?: string;
  readonly raisedAt: string;
}

export interface FleetAlertPage {
  readonly items: readonly FleetAlert[];
  readonly cursor: string | null;
  readonly hasMore: boolean;
}

/* ---------------------------------------------------------------------------
 * Schedules — read for the dashboard's "not started" row, written by SCR-FP-008
 * ------------------------------------------------------------------------ */

/**
 * `fleet.yaml#/components/schemas/FleetSchedule`, narrowed to what the dashboard
 * counts.
 *
 * SCR-FP-008 (C115) owns the screen; this is the one row of it the dashboard's
 * alerts card needs. `MISSED` is fleet-svc's own verdict — the not-started alarm
 * fired and `alarmRaisedAt` was written — so counting those rows reports an alarm
 * the platform raised rather than raising one here.
 */
export type FleetScheduleStatus = 'SCHEDULED' | 'STARTED' | 'MISSED' | 'CANCELLED';

export interface FleetSchedule {
  readonly scheduleId: string;
  readonly vehicleId: string;
  readonly routeId?: string;
  readonly departAt: string;
  readonly notStartedAlarmMinutes?: number;
  readonly status: FleetScheduleStatus;
  readonly alarmRaisedAt?: string;
}

export interface FleetScheduleList {
  readonly items: readonly FleetSchedule[];
}

/* ---------------------------------------------------------------------------
 * Rendering
 * ------------------------------------------------------------------------ */

/** One row of the dashboard's alerts card: a count, a tone and a sentence. */
export interface AlertRow {
  readonly key: string;
  readonly labelKey: FleetMessageKey;
  readonly count: number;
  readonly tone: StatusTone;
  /** Where an operator goes to do something about it, or `null` if nowhere yet. */
  readonly href: string | null;
}

/** A count's chip tone: neutral at zero, so a clean fleet does not glow red. */
export function countTone(count: number, raised: StatusTone): StatusTone {
  return count > 0 ? raised : 'neutral';
}
