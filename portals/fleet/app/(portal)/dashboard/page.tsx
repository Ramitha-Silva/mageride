import { redirect } from 'next/navigation';

import { read } from '@/api/client';
import {
  DASHBOARD_INVOICE_LIMIT,
  FLEET_BILLING,
  FLEET_WALLET,
  invoiceStatusView,
  nextPayableInvoice,
  type FleetInvoicePage,
  type FleetWallet,
} from '@/api/billing';
import {
  colomboToday,
  FLEET_ALERTS,
  FLEET_ANALYTICS,
  FLEET_SCHEDULES,
  singleDay,
  type FleetAlertPage,
  type FleetAnalyticsAnswer,
  type FleetScheduleList,
} from '@/api/insights';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { FLEET_HEALTH, type FleetHealthRollup } from '@/api/trackers';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { KpiTiles } from '@/components/KpiTiles';
import { ProblemPanel } from '@/components/ProblemPanel';
import { AlertsCard } from '@/components/dashboard/AlertsCard';
import { WalletCard, WalletUnavailable } from '@/components/dashboard/WalletCard';
import {
  activeVehicleCount,
  alertRows,
  deviceDownWindow,
  todaysTrips,
} from '@/components/dashboard/dashboard-model';
import { formatDay, formatFareMinor, formatInstant } from '@/i18n/format';
import { getLocale, getTranslator } from '@/i18n/server';
import type { FleetTranslator, Locale } from '@/i18n';
import { billingRefusal, canReadBilling } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-003 · `fleet_dashboard`** — the console's landing screen: how many
 * vehicles are reporting, how many trips today, what needs attention, and what the
 * organisation owes.
 *
 * ## Six reads, three services, and only one of them can take the screen down
 *
 * | card | route | owner | if it fails |
 * |---|---|---|---|
 * | Online / Stale / Offline | `GET …/health` | fleet-health-svc | the screen shows a problem panel |
 * | Trips today | `GET …/analytics?from=to=today` | fleet-svc | the tile reads zero and says so |
 * | Alerts · not started | `GET …/schedules` | fleet-svc | the row is absent |
 * | Alerts · Phase 3 | `GET …/alerts` | fleet-svc | the sentence stands on its own |
 * | Active vehicles, docs, mode split | `GET …/vehicles` | fleet-svc | the denominators drop |
 * | Wallet & next invoice | `GET …/wallet`, `…/billing` | fleet-billing-svc | the card is a sentence |
 *
 * The health rollup is the screen — three of the four KPI tiles are its counts —
 * so its failure is the screen's. Everything else degrades to a tile with less on
 * it, because a dashboard that goes blank because one of six services is slow is
 * a dashboard nobody trusts the next time it is blank.
 *
 * ## "Today" is a Colombo day the screen names, not a default it inherits
 *
 * `GET …/analytics` with no range reports the last thirty days. The tile says
 * *today*, so the screen sends `from=to=` today in **Asia/Colombo** (D-13). A Next
 * container runs in UTC: without that, between 18:30 and midnight — the evening an
 * operator is most likely to be looking — "trips today" would be yesterday's.
 *
 * ## Two things the sketch draws that are not here, and one that is
 *
 * The wireframe's **"Insurance expiring (30 d)"** row has no fleet-wide read
 * behind it (see `dashboard-model`), and the card says so beside the row that
 * replaces it. The wireframe's **"Top up wallet"** button is a link to SCR-FP-010
 * rather than a top-up on the dashboard, because a payment session belongs on the
 * screen that owns it. What *is* here and not in the sketch is US-3.16's
 * device-down banner, which is the one fleet alert on this platform with a
 * producer.
 */

export const dynamic = 'force-dynamic';

export default async function DashboardPage() {
  const session = await getSession();
  if (!session) redirect('/login');

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const today = singleDay(colomboToday());

  let health: FleetHealthRollup | null = null;
  let problem: ProblemDetails | null = null;
  try {
    health = await read<FleetHealthRollup>({ org: FLEET_HEALTH });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const [trips, schedules, roster, phaseThree] = await Promise.all([
    optional<FleetAnalyticsAnswer>({
      org: FLEET_ANALYTICS,
      searchParams: { from: today.from, to: today.to },
    }),
    optional<FleetScheduleList>({ org: FLEET_SCHEDULES }),
    optional<FleetVehicleList>({ org: VEHICLES }),
    // Read so the empty state is the platform's answer rather than this screen's
    // assumption. `ListAlertsAsync` returns `([], null, false)` by construction in
    // Phase 1; the card says so in words either way.
    optional<FleetAlertPage>({ org: FLEET_ALERTS }),
  ]);

  // The two fleet-billing-svc reads are made **only** once `canReadBilling` has
  // said the caller may — three 403s on every dashboard load would be this portal
  // generating the audit trail of an access attempt nobody made. They are awaited
  // here rather than inside the card, because an async component is something
  // only Next can render and the card is a component this build's tests render.
  const billing = canReadBilling(session) ? await walletAndInvoice() : null;

  const vehicles: readonly FleetVehicle[] = roster?.items ?? [];
  const counted = todaysTrips(trips?.items ?? [], vehicles);
  const active = roster ? activeVehicleCount(vehicles) : (health?.counts.total ?? 0);

  const window = deviceDownWindow(health);

  return (
    <div className="flex flex-col gap-md">
      <h2 className="text-title font-display">{t('fleet.dashboard.title')}</h2>

      {problem ? <ProblemPanel problem={problem} /> : null}

      <KpiTiles
        tiles={[
          {
            key: 'online',
            label: t('fleet.dashboard.kpi.online'),
            value: String(health?.counts.online ?? 0),
            tone: 'success',
            detail: roster
              ? t('fleet.dashboard.kpi.ofVehicles', { count: active })
              : t('fleet.dashboard.kpi.ofTrackers', { count: health?.counts.total ?? 0 }),
          },
          {
            key: 'stale',
            label: t('fleet.dashboard.kpi.stale'),
            value: String(health?.counts.stale ?? 0),
            tone: 'warning',
            // The deployment's own threshold, not US-3.13's 5 minutes hardcoded:
            // fleet-health-svc returns `thresholds` so "a retuned deployment does
            // not need a portal release".
            detail: t('fleet.dashboard.kpi.staleAfter', {
              minutes: Math.round((health?.thresholds.staleAfterSeconds ?? 300) / 60),
            }),
          },
          {
            key: 'offline',
            label: t('fleet.dashboard.kpi.offline'),
            value: String(health?.counts.offline ?? 0),
            tone: 'error',
            detail: t('fleet.dashboard.kpi.offlineAfter', {
              minutes: Math.round((health?.thresholds.offlineAfterSeconds ?? 1800) / 60),
            }),
          },
          {
            key: 'trips',
            label: t('fleet.dashboard.kpi.trips'),
            value: String(counted.total),
            detail: counted.complete
              ? t('fleet.dashboard.kpi.modeSplit', { a: counted.modeA, b: counted.modeB })
              : t('fleet.dashboard.kpi.noModeSplit'),
          },
        ]}
      />

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <AlertsCard
          rows={alertRows({
            health,
            schedules: schedules?.items ?? [],
            vehicles,
            rosterReadable: roster !== null,
          }).map((row) => ({
            key: row.key,
            label: t(row.labelKey),
            count: String(row.count),
            tone: row.tone,
            href: row.href,
          }))}
          banner={
            window
              ? t('fleet.dashboard.alert.deviceDown', {
                  offline: window.offlineVehicles,
                  expected: window.expectedVehicles,
                  minutes: window.windowMinutes,
                  threshold: window.thresholdPct,
                })
              : null
          }
          labels={{
            heading: t('fleet.dashboard.alerts.heading'),
            phaseThree: t('fleet.dashboard.alerts.phaseThree', {
              count: phaseThree?.items.length ?? 0,
            }),
            noExpiryRow: t('fleet.dashboard.alerts.noExpiryRow'),
          }}
        />

        {billing?.wallet ? (
          walletCard(billing.wallet, billing.invoices, locale, t)
        ) : (
          <WalletUnavailable
            heading={t('fleet.dashboard.wallet.heading')}
            reason={walletRefusal(session, billing !== null, t)}
          />
        )}
      </div>

      <p className="text-caption text-outline-variant">{asOfSentence(health, locale, t)}</p>
    </div>
  );
}

/** fleet-billing-svc's two reads, made only for a caller who may make them. */
async function walletAndInvoice(): Promise<{
  wallet: FleetWallet | null;
  invoices: FleetInvoicePage | null;
}> {
  const [wallet, invoices] = await Promise.all([
    optional<FleetWallet>({ org: FLEET_WALLET }),
    optional<FleetInvoicePage>({
      org: FLEET_BILLING,
      searchParams: { limit: DASHBOARD_INVOICE_LIMIT },
    }),
  ]);

  return { wallet, invoices };
}

/**
 * Why there is a sentence where the card would be.
 *
 * Three states, and they are three different things to say. `attempted` is true
 * when the caller was entitled and the read failed anyway — a transient fault,
 * not a permission — and the sentence says the rest of the screen is still good.
 */
function walletRefusal(
  session: Parameters<typeof billingRefusal>[0],
  attempted: boolean,
  t: FleetTranslator,
): string {
  if (attempted) return t('fleet.dashboard.wallet.unavailable');
  return billingRefusal(session) === 'pending-org'
    ? t('fleet.dashboard.wallet.pendingOrg')
    : t('fleet.dashboard.wallet.ownerOnly');
}

function walletCard(
  wallet: FleetWallet,
  invoices: FleetInvoicePage | null,
  locale: Locale,
  t: FleetTranslator,
) {
  const invoice = nextPayableInvoice(invoices?.items);
  const status = invoice ? invoiceStatusView(invoice.status) : null;

  return (
    <WalletCard
      balance={t('fleet.money.rupees', { amount: formatFareMinor(locale, wallet.balanceMinor) })}
      outstanding={t('fleet.money.rupees', {
        amount: formatFareMinor(locale, wallet.outstandingMinor),
      })}
      available={t('fleet.money.rupees', {
        amount: formatFareMinor(locale, wallet.availableMinor),
      })}
      invoice={
        invoice && status
          ? {
              period:
                formatDay(locale, `${invoice.periodMonth}T00:00:00Z`) ?? invoice.periodMonth,
              amount: t('fleet.money.rupees', {
                amount: formatFareMinor(locale, invoice.amountMinor),
              }),
              status: t(status.labelKey),
              statusTone: status.tone,
              vehicleCount:
                invoice.vehicleCount === undefined
                  ? null
                  : t('fleet.dashboard.wallet.vehicleLines', { count: invoice.vehicleCount }),
              due: invoice.dueAt
                ? t('fleet.dashboard.wallet.dueAt', {
                    date: formatDay(locale, invoice.dueAt) ?? invoice.dueAt,
                  })
                : null,
            }
          : null
      }
      labels={{
        heading: t('fleet.dashboard.wallet.heading'),
        balance: t('fleet.dashboard.wallet.balance'),
        outstanding: t('fleet.dashboard.wallet.outstanding'),
        available: t('fleet.dashboard.wallet.available'),
        nextInvoice: t('fleet.dashboard.wallet.nextInvoice'),
        topUp: t('fleet.dashboard.wallet.topUp'),
        modeANote: t('fleet.dashboard.wallet.modeANote'),
        nothingDue: t('fleet.dashboard.wallet.nothingDue'),
      }}
    />
  );
}

/** A read whose failure costs a card or a denominator rather than the screen. */
async function optional<T>(options: {
  readonly org: string;
  readonly searchParams?: Readonly<Record<string, string | number>>;
}): Promise<T | null> {
  try {
    return await read<T>(options);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}

function asOfSentence(
  health: FleetHealthRollup | null,
  locale: Locale,
  t: FleetTranslator,
): string {
  const time = formatInstant(locale, health?.asOf);
  return time ? t('fleet.dashboard.asOf', { time }) : t('fleet.dashboard.asOfUnknown');
}
