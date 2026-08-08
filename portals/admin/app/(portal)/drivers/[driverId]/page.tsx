import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import {
  driverPath,
  driverSelection,
  DRIVER_TABS,
  isDirectoryId,
  tabSelection,
  type DriverDetail,
  type DriverTab,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ActivityPanel } from '@/components/directories/ActivityPanel';
import { DetailHeader } from '@/components/directories/DetailHeader';
import { Handoffs, type HandoffView } from '@/components/directories/Handoffs';
import { LinkedVehicles } from '@/components/directories/LinkedVehicles';
import {
  dailyFeeRows,
  driverFacts,
  driverStatusPill,
  reportRows,
  transferRows,
  tripRows,
  vehicleChips,
  walletRows,
  type RenderContext,
  type TableRowView,
} from '@/components/directories/model';
import { driverHref, driversHref, menuPath, tabHref } from '@/components/directories/links';
import { ProfileCard } from '@/components/directories/ProfileCard';
import type { AdminMessageKey } from '@/i18n';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-013 · `driver_detail`** — a driver's profile, wallet, level and linked
 * vehicles, with Trips / Wallet ledger / Daily fee / Credit transfers / Reports
 * (AL-41, US-24.10, BR-28.8).
 *
 * ## Half of this screen is money, and none of it moves any
 *
 * The wireframe says "Finance can post a reversal from here (SCR-AP-006)". They
 * can, and this is not where: `POST /v1/admin/drivers/wallet/{driverId}/reverse-fee`
 * is gated on the Driver-wallet-adjustments row and lives on its own screen, so
 * what this record offers is a **link with the driver already in the box** — the
 * same shape SCR-AP-004's suspend card is aimed by, and drawn only for an operator
 * whose menu carries that screen. A button here would be a control gated on a row
 * this screen is not behind.
 *
 * ## Why Finance can open this screen at all
 *
 * `searchDrivers` and `getDriverDetail` are gated on URD §2.3's **Driver wallet &
 * credit transfers** row — the only row that carries Finance with a read — rather
 * than on the Driver-app row that would be the natural fit and gives Finance ➖.
 * BR-28.8 names the Finance Officer, and half of this screen is the wallet ledger,
 * the daily fee and the credit transfers; a Finance Officer refused the driver
 * directory could not reconcile the wallet they are told to reconcile.
 *
 * ## Documents are the verification screen's, and stay there
 *
 * `DriverDetail` carries no documents — a licence lives behind AL-39's viewer with
 * its own `DOC_VIEW` row and its own nav item. The wireframe's "View documents"
 * button is therefore a link to `/verification/{driverId}`, drawn when the caller
 * holds the queues and absent when they do not.
 */

export const dynamic = 'force-dynamic';

const TAB_LABELS: Readonly<Record<DriverTab, AdminMessageKey>> = {
  trips: 'admin.directory.tab.trips',
  wallet: 'admin.directory.tab.wallet',
  dailyFee: 'admin.directory.tab.dailyFee',
  transfers: 'admin.directory.tab.transfers',
  reports: 'admin.directory.tab.reports',
};

export default async function DriverDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ driverId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { driverId } = await params;
  const query = await searchParams;

  if (!isDirectoryId(driverId)) notFound();

  const selection = driverSelection(query);
  const tab = tabSelection(DRIVER_TABS, query);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  let detail: DriverDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<DriverDetail>({ path: driverPath(driverId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const back = driversHref(selection);

  if (!detail) {
    return (
      <div className="flex flex-col gap-md">
        <DetailHeader
          backHref={back}
          backLabel={t('admin.directory.backToDrivers')}
          title={t('admin.directory.driver.unavailable')}
          subjectId={driverId}
        />
        {problem ? <ProblemPanel problem={problem} /> : null}
      </div>
    );
  }

  const { profile } = detail;
  const menu = session?.menu ?? [];
  const vehiclesPath = menuPath(menu, 'vehicles');
  const verificationPath = menuPath(menu, 'verification');
  const adjustmentsPath = menuPath(menu, 'wallet-adjustments');
  const reportsPath = menuPath(menu, 'reports');

  const handoffs: HandoffView[] = [
    ...(verificationPath
      ? [
          {
            key: 'documents',
            href: `${verificationPath}/${driverId}`,
            label: t('admin.directory.handoff.documents'),
            hint: t('admin.directory.handoff.documentsHint'),
          },
        ]
      : []),
    ...(adjustmentsPath
      ? [
          {
            key: 'reversal',
            href: `${adjustmentsPath}?driverId=${driverId}`,
            label: t('admin.directory.handoff.reversal'),
            hint: t('admin.directory.handoff.reversalHint'),
          },
        ]
      : []),
    ...(reportsPath
      ? [
          {
            key: 'suspend',
            href: `${reportsPath}?subject=driver&subjectId=${driverId}#suspend`,
            label: t('admin.directory.handoff.suspendDriver'),
            hint: t('admin.directory.handoff.suspendHint'),
          },
        ]
      : []),
  ];

  const detailUrl = driverHref(selection, driverId);
  const panel = PANELS[tab](detail, context);

  return (
    <div className="flex flex-col gap-md">
      <DetailHeader
        backHref={back}
        backLabel={t('admin.directory.backToDrivers')}
        title={profile.name}
        subjectId={profile.driverId}
        pill={driverStatusPill(profile.status, t)}
      />

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <ProfileCard
          heading={t('admin.directory.driver.profile')}
          facts={driverFacts(profile, context)}
          note={t('admin.directory.piiNotice')}
        >
          <LinkedVehicles
            vehicles={vehicleChips(
              detail.vehicles ?? [],
              vehiclesPath ? (vehicleId) => `${vehiclesPath}/${vehicleId}` : null,
              t,
            )}
            labels={{
              heading: t('admin.directory.driver.vehicles'),
              empty: t('admin.directory.driver.noVehicles'),
            }}
          />

          <Handoffs heading={t('admin.directory.handoff.heading')} items={handoffs} />
        </ProfileCard>

        <ActivityPanel
          navLabel={t('admin.directory.tabs.label')}
          tabs={DRIVER_TABS.map((id) => ({
            id,
            href: tabHref(detailUrl, id, DRIVER_TABS[0]),
            label: t(TAB_LABELS[id]),
            current: id === tab,
          }))}
          rows={panel.rows}
          labels={{
            caption: t(TAB_LABELS[tab]),
            columns: panel.columns,
            empty: panel.empty,
          }}
          {...(panel.note ? { note: panel.note } : {})}
        />
      </div>
    </div>
  );
}

interface PanelView {
  readonly columns: readonly string[];
  readonly rows: readonly TableRowView[];
  readonly empty: string;
  readonly note?: string;
}

const PANELS: Readonly<Record<DriverTab, (detail: DriverDetail, context: RenderContext) => PanelView>> = {
  trips: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.journey'),
      context.t('admin.directory.field.regNo'),
      context.t('admin.directory.column.passenger'),
      context.t('admin.directory.column.fare'),
      context.t('admin.directory.column.state'),
    ],
    rows: tripRows(detail.trips ?? [], context),
    empty: context.t('admin.directory.trip.empty'),
    note: context.t('admin.directory.trip.note'),
  }),
  wallet: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.entry'),
      context.t('admin.directory.column.amount'),
      context.t('admin.directory.column.balanceAfter'),
    ],
    rows: walletRows(detail.walletLedger ?? [], context),
    empty: context.t('admin.directory.wallet.empty'),
    note: context.t('admin.directory.wallet.note'),
  }),
  dailyFee: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.feeDate'),
      context.t('admin.directory.column.vehicle'),
      context.t('admin.directory.column.amount'),
      context.t('admin.directory.column.tripsThatDay'),
      context.t('admin.directory.column.charged'),
      context.t('admin.directory.column.status'),
    ],
    rows: dailyFeeRows(detail.dailyFee ?? [], context),
    empty: context.t('admin.directory.fee.empty'),
    note: context.t('admin.directory.fee.note'),
  }),
  transfers: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.direction'),
      context.t('admin.directory.column.counterparty'),
      context.t('admin.directory.column.amount'),
      context.t('admin.directory.column.initiation'),
      context.t('admin.directory.column.status'),
    ],
    rows: transferRows(detail.creditTransfers ?? [], context),
    empty: context.t('admin.directory.transfer.empty'),
    note: context.t('admin.directory.transfer.note'),
  }),
  reports: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.raised'),
      context.t('admin.directory.column.vehicle'),
      context.t('admin.directory.column.reason'),
      context.t('admin.directory.column.status'),
    ],
    rows: reportRows(detail.reports ?? [], context),
    empty: context.t('admin.directory.report.empty'),
    note: context.t('admin.directory.report.note'),
  }),
};
