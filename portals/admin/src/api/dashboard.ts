/**
 * SCR-AP-002's wire shapes, and the one query that produces them.
 *
 * Transcribed from `backend/contracts/admin-bff.yaml` — `getAdminDashboardStats`,
 * `exportAdminDashboardStats`, and the `DashboardKpis` / `DashboardDeltas` /
 * `DashboardLive` schemas the two share. They live here rather than in
 * `./types.ts` for the reason that file states: the shell owns the shapes the
 * *shell* needs, and a portal-wide types module that grew every screen's payload
 * would make each component edit a file it does not own.
 *
 * **Two paths, one query.** The JSON and the CSV take the same three parameters
 * and admin-bff renders both from one `IDashboardStatsService` call — "exactly
 * the figures `/dashboard/stats` returns for the same query" is the contract's own
 * description of the export. Which is why {@link statsSearch} is the only place
 * this portal builds that query: the figures on the screen and the figures in the
 * download are the same figures because they are the same request.
 */

/** `GET /v1/admin/dashboard/stats` — the period filter (AL-38, US-24.7). */
export const STATS_PATH = '/v1/admin/dashboard/stats';

/** `GET /v1/admin/dashboard/stats.csv` — the same figures as a download. */
export const STATS_CSV_PATH = '/v1/admin/dashboard/stats.csv';

/** `admin-bff.yaml#/components/parameters/StatsPeriod`, in the contract's order. */
export const STATS_PERIODS = ['today', 'week', 'month', 'custom'] as const;

export type StatsPeriod = (typeof STATS_PERIODS)[number];

/** The four period figures plus the daily fee, all as the contract names them. */
export interface DashboardKpis {
  readonly completedTrips: number;
  /** Integer minor units (CLAUDE.md, Money as minor units). */
  readonly grossFareMinor: number;
  readonly newRiders: number;
  readonly newDrivers: number;
  /** Integer minor units. */
  readonly dailyFeeRevenueMinor: number;
}

/**
 * Percentage change against the immediately preceding period of the same length.
 *
 * **Every property is optional, and that is load-bearing.** C061 answers `null`
 * rather than `0` or `100` when the previous period was empty — growth from
 * nothing has no percentage — so an absent value means "no comparison exists",
 * which is a different statement from "no change". Rendering it as 0 % would be
 * the portal inventing the one number the read model refused to invent.
 */
export interface DashboardDeltas {
  readonly completedTripsPct?: number | null;
  readonly grossFarePct?: number | null;
  readonly newRidersPct?: number | null;
  readonly newDriversPct?: number | null;
  readonly dailyFeeRevenuePct?: number | null;
}

/**
 * The real-time block. **It does not move with the period filter** (AL-38,
 * D6' §I-28.5): these are facts about this instant, so filtering the dashboard to
 * last March must leave all three where they are.
 */
export interface DashboardLive {
  readonly onlineDrivers: number;
  readonly pendingVerifications: number;
  readonly openTickets: number;
}

/** An inclusive pair of Asia/Colombo business dates, `YYYY-MM-DD`. */
export interface StatsRange {
  readonly from: string;
  readonly to: string;
}

export interface DashboardStats {
  readonly period: StatsPeriod;
  readonly range: StatsRange;
  readonly kpis: DashboardKpis;
  readonly deltaVsPrev: DashboardDeltas;
  readonly live: DashboardLive;
}

/**
 * What the filter bar is currently asking for, resolved from the URL.
 *
 * `awaitingRange` is the state a naive implementation does not have: **`custom`
 * selected, with no usable pair of dates yet**. It exists because the two obvious
 * alternatives are both wrong. Substituting today's figures would put the wrong
 * number under the right heading and the operator would have no way to tell —
 * C061 refuses to do it server-side for exactly that reason, and a portal that did
 * it first would defeat the refusal. Sending the incomplete query instead would
 * answer the operator's first click on "Custom range" with a validation error
 * about a form they have not filled in yet.
 *
 * So nothing is asked of admin-bff, no figures are drawn, and the date form is
 * shown waiting. A range that is *complete but impossible* — `to` before `from`,
 * a window longer than the year the read model admits — is still the server's to
 * refuse: that is policy, and the portal does not hold a second copy of it.
 */
export interface StatsSelection {
  readonly period: StatsPeriod;
  /** Carried only when the period is `custom` and both ends parsed. */
  readonly from?: string;
  readonly to?: string;
  readonly awaitingRange: boolean;
}

const BUSINESS_DATE = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Whether a value is a `_shared.yaml#/components/schemas/BusinessDate`.
 *
 * The round trip through `Date` is what rejects `2026-02-31`: the pattern alone
 * admits it, and `<input type="date">` is not the only thing that reaches this —
 * the query string is whatever an operator pasted or a bookmark preserved.
 */
export function isBusinessDate(value: string | undefined): value is string {
  if (!value || !BUSINESS_DATE.test(value)) return false;

  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().startsWith(value);
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

function isStatsPeriod(value: string | undefined): value is StatsPeriod {
  return value !== undefined && (STATS_PERIODS as readonly string[]).includes(value);
}

/**
 * The filter's state, from the page's own search parameters.
 *
 * An unrecognised `period` falls back to `today`, which is the contract's default
 * and the only period the read model will resolve without further input. That is
 * not the silent substitution above: there is no window a `?period=quarter` could
 * have meant, so there is no figure to put under the wrong heading.
 */
export function statsSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): StatsSelection {
  const requested = first(params.period);
  const period: StatsPeriod = isStatsPeriod(requested) ? requested : 'today';

  if (period !== 'custom') return { period, awaitingRange: false };

  const from = first(params.from);
  const to = first(params.to);

  if (!isBusinessDate(from) || !isBusinessDate(to)) {
    return { period, awaitingRange: true };
  }

  return { period, from, to, awaitingRange: false };
}

/**
 * The selection as `?period=&from=&to=` — for `read()`, for the export, and for
 * the links the filter bar draws. One function, so the screen and its download
 * cannot drift apart.
 *
 * `from`/`to` are dropped on the three fixed periods rather than passed and
 * ignored: admin-bff ignores them, and a URL that carried a range the figures do
 * not reflect is a URL somebody will later read as evidence of what was on screen.
 */
export function statsSearch(selection: StatsSelection): Record<string, string> {
  if (selection.period !== 'custom' || !selection.from || !selection.to) {
    return { period: selection.period };
  }

  return { period: selection.period, from: selection.from, to: selection.to };
}

/** `path` with this selection's query on it. */
export function statsHref(path: string, selection: StatsSelection): string {
  return `${path}?${new URLSearchParams(statsSearch(selection)).toString()}`;
}
