import type {
  DashboardDeltas,
  DashboardKpis,
  DashboardLive,
  DashboardStats,
} from '@/api/dashboard';
import type { AdminMenuGroup } from '@/api/types';
import type { AdminMessageKey, AdminTranslator } from '@/i18n';
import { formatCount, formatMinorUnits, formatPercent } from '@/i18n/format';
import { permittedItems } from '@/server/access';

import type { Locale } from '@mageride/i18n';

/**
 * SCR-AP-002 as data: what the cards say, before anything draws them.
 *
 * The same shape as C104's `nav-model.ts`, for the same two reasons. The
 * components stay dumb enough to render on the server *and* in a jsdom test with
 * no framework around them; and the decisions worth arguing about — an absent
 * delta is not a zero, a live card has no previous period, an alert links
 * somewhere only if the operator's menu says they may go there — end up in one
 * pure function a test can drive with a fixture instead of scattered through JSX.
 *
 * **The four period cards and the three live ones come from the same payload and
 * mean different things.** `kpis` is the window the filter chose and moves with
 * it; `live` is this instant and does not (AL-38, D6' §I-28.5). They are drawn
 * apart, under their own headings, because a filter that visibly changed five
 * figures and left three alone would otherwise look broken.
 */

export type DeltaDirection = 'up' | 'down' | 'flat' | 'unknown';

/** Glyphs, not copy — a triangle is a triangle in all three languages. */
const ARROWS: Readonly<Record<DeltaDirection, string>> = {
  up: '▲',
  down: '▼',
  flat: '',
  unknown: '—',
};

const DIRECTION_KEYS: Readonly<Record<DeltaDirection, AdminMessageKey>> = {
  up: 'admin.dashboard.delta.up',
  down: 'admin.dashboard.delta.down',
  flat: 'admin.dashboard.delta.flat',
  unknown: 'admin.dashboard.delta.unknown',
};

export interface DeltaView {
  readonly key: string;
  readonly direction: DeltaDirection;
  /** `▲ 9.2%` — shown, and `aria-hidden`, because {@link description} says it in words. */
  readonly display: string;
  /** The whole sentence, naming its metric. The only thing a screen reader is given. */
  readonly description: string;
  /**
   * Which figure this is about, shown only on a card carrying more than one — the
   * riders/drivers card, which the wireframe draws as a single tile.
   */
  readonly qualifier?: string;
}

export interface StatView {
  readonly key: string;
  readonly label: string;
  readonly value: string;
  /** Empty on a live card: there is no previous period for a fact about this second. */
  readonly deltas: readonly DeltaView[];
}

export interface AlertView {
  readonly key: string;
  readonly label: string;
  readonly count: number;
  /** The count, grouped for the locale. Shown, and `aria-hidden`. */
  readonly display: string;
  /** The count in words, for the screen reader a bare "38" tells nothing. */
  readonly description: string;
  /**
   * Where the work is done, when the caller's menu carries that module. Absent for
   * an operator who can see the count and may not open the queue — deny-by-default
   * is the menu (AL-06), and a link this portal drew itself would be one the proxy
   * then refuses.
   */
  readonly href?: string;
}

export interface DashboardView {
  readonly period: readonly StatView[];
  readonly live: readonly StatView[];
  readonly alerts: readonly AlertView[];
}

/**
 * One `deltaVsPrev` percentage.
 *
 * **`null`, `undefined` and `0` are three different answers and stay that way.**
 * C061 answers null when the previous period was empty — "growth from nothing has
 * no percentage" — so an absent value becomes `unknown` and renders as a dash. A
 * zero is a real comparison that found no change. Collapsing the first into the
 * second is the portal inventing the number the read model declined to invent.
 */
function delta(
  key: string,
  metric: string,
  percentagePoints: number | null | undefined,
  t: AdminTranslator,
  locale: Locale,
  qualifier?: string,
): DeltaView {
  // `NaN` and `Infinity` join `null` and `undefined` in "there is no comparison":
  // a JSON body carrying either is a figure nobody can act on, and a card reading
  // "▲ NaN%" is worse than one reading "—".
  const points =
    typeof percentagePoints === 'number' && Number.isFinite(percentagePoints)
      ? percentagePoints
      : null;

  const direction: DeltaDirection =
    points === null ? 'unknown' : points > 0 ? 'up' : points < 0 ? 'down' : 'flat';

  const value = points === null ? '' : formatPercent(locale, points);
  const display = points === null ? ARROWS.unknown : `${ARROWS[direction]} ${value}`.trim();

  return {
    key,
    direction,
    display,
    description: t(DIRECTION_KEYS[direction], { metric, value }),
    ...(qualifier ? { qualifier } : {}),
  };
}

function periodCards(
  kpis: DashboardKpis,
  deltas: DashboardDeltas,
  t: AdminTranslator,
  locale: Locale,
): StatView[] {
  const riders = t('admin.dashboard.kpi.newRiders');
  const drivers = t('admin.dashboard.kpi.newDrivers');

  return [
    {
      key: 'completedTrips',
      label: t('admin.dashboard.kpi.completedTrips'),
      value: formatCount(locale, kpis.completedTrips),
      deltas: [
        delta(
          'completedTrips',
          t('admin.dashboard.kpi.completedTrips'),
          deltas.completedTripsPct,
          t,
          locale,
        ),
      ],
    },
    {
      key: 'grossFare',
      label: t('admin.dashboard.kpi.grossFare'),
      value: t('admin.dashboard.money', { amount: formatMinorUnits(locale, kpis.grossFareMinor) }),
      deltas: [
        delta('grossFare', t('admin.dashboard.kpi.grossFare'), deltas.grossFarePct, t, locale),
      ],
    },
    {
      // `web_admin.html` draws one tile for the pair, and the contract carries a
      // delta for each. Both are shown, qualified, rather than dropping the one
      // the wireframe's sketch had no room for.
      key: 'newRidersDrivers',
      label: t('admin.dashboard.kpi.newRidersDrivers'),
      value: `${formatCount(locale, kpis.newRiders)} / ${formatCount(locale, kpis.newDrivers)}`,
      deltas: [
        delta('newRiders', riders, deltas.newRidersPct, t, locale, t('admin.dashboard.kpi.riders')),
        delta(
          'newDrivers',
          drivers,
          deltas.newDriversPct,
          t,
          locale,
          t('admin.dashboard.kpi.drivers'),
        ),
      ],
    },
    {
      key: 'dailyFeeRevenue',
      label: t('admin.dashboard.kpi.dailyFeeRevenue'),
      value: t('admin.dashboard.money', {
        amount: formatMinorUnits(locale, kpis.dailyFeeRevenueMinor),
      }),
      deltas: [
        delta(
          'dailyFeeRevenue',
          t('admin.dashboard.kpi.dailyFeeRevenue'),
          deltas.dailyFeeRevenuePct,
          t,
          locale,
        ),
      ],
    },
  ];
}

function liveCards(live: DashboardLive, t: AdminTranslator, locale: Locale): StatView[] {
  return [
    {
      key: 'onlineDrivers',
      label: t('admin.dashboard.kpi.onlineDrivers'),
      value: formatCount(locale, live.onlineDrivers),
      deltas: [],
    },
    {
      key: 'pendingVerifications',
      label: t('admin.dashboard.kpi.pendingVerifications'),
      value: formatCount(locale, live.pendingVerifications),
      deltas: [],
    },
    {
      key: 'openTickets',
      label: t('admin.dashboard.kpi.openTickets'),
      value: formatCount(locale, live.openTickets),
      deltas: [],
    },
  ];
}

/** The path admin-bff gave this nav item, or `undefined` if the caller has no such item. */
function screenPath(menu: readonly AdminMenuGroup[], navKey: string): string | undefined {
  return permittedItems(menu).find(({ item }) => item.key === navKey)?.item.path;
}

/**
 * The alerts feed: the queues with work waiting in them, and nothing else.
 *
 * **Two rows and a source note.** `web_admin.html` illustrates this card with
 * "tracker offline > 15 min", "duplicate IMEI quarantine" and "COD uncollected
 * > 24h", and **no contract on the platform serves any of the three** —
 * `admin-bff.yaml` has no alerts route, and AL-02 keeps this console inside
 * `/v1/admin/**`, so there is nowhere else to ask. What the dashboard payload does
 * carry is two counters that *are* outstanding work, and they are what the feed is
 * built from. The gap is recorded in the C105 handoff rather than filled with a
 * card of invented rows.
 *
 * A row appears only when its count is above zero, which is what makes this a list
 * of things to do rather than a second copy of the live cards beside it. Every row
 * is `warning`: the payload carries counts and no severity, and a threshold that
 * turned 50 tickets red would be this portal deciding an operations policy.
 */
function alerts(
  live: DashboardLive,
  menu: readonly AdminMenuGroup[],
  t: AdminTranslator,
  locale: Locale,
): AlertView[] {
  const rows: { key: string; navKey: string; label: string; count: number }[] = [
    {
      key: 'pendingVerifications',
      navKey: 'verification',
      label: t('admin.dashboard.alerts.verification'),
      count: live.pendingVerifications,
    },
    {
      key: 'openTickets',
      navKey: 'support-tickets',
      label: t('admin.dashboard.alerts.tickets'),
      count: live.openTickets,
    },
  ];

  return rows
    .filter((row) => row.count > 0)
    .map(({ key, navKey, label, count }) => {
      const href = screenPath(menu, navKey);
      return {
        key,
        label,
        count,
        display: formatCount(locale, count),
        description: t('admin.dashboard.alerts.count', { count }),
        ...(href ? { href } : {}),
      };
    });
}

export function buildDashboardView(
  stats: DashboardStats,
  menu: readonly AdminMenuGroup[],
  t: AdminTranslator,
  locale: Locale,
): DashboardView {
  return {
    period: periodCards(stats.kpis, stats.deltaVsPrev, t, locale),
    live: liveCards(stats.live, t, locale),
    alerts: alerts(stats.live, menu, t, locale),
  };
}
