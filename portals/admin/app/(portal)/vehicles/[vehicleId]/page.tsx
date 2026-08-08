import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import {
  isDirectoryId,
  tabSelection,
  vehiclePath,
  vehicleSelection,
  VEHICLE_TABS,
  type AdminVehicleDetail,
  type VehicleTab,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ActivityPanel } from '@/components/directories/ActivityPanel';
import { DetailHeader } from '@/components/directories/DetailHeader';
import { Handoffs, type HandoffView } from '@/components/directories/Handoffs';
import {
  dailyFeeRows,
  dispatchStatePill,
  earningsRows,
  reportRows,
  tripRows,
  vehicleFacts,
  vehicleHeadline,
  type RenderContext,
  type TableRowView,
} from '@/components/directories/model';
import {
  menuPath,
  tabHref,
  vehicleDocHref,
  vehicleHref,
  vehicleMediaHref,
  vehiclesHref,
} from '@/components/directories/links';
import { ProfileCard } from '@/components/directories/ProfileCard';
import { DocumentGrid } from '@/components/verification/DocumentGrid';
import { documentTiles } from '@/components/verification/model';
import type { AdminMessageKey } from '@/i18n';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-015 · `vehicle_detail`** — registration, insurance, revenue licence and
 * tracker, the document thumbnails, and Trips / Earnings / Daily fee / Reports
 * (AL-42, US-24.11).
 *
 * ## The thumbnails open the shared viewer, and share it in the strongest sense
 *
 * `DocumentGrid`, `documentTiles` and `DocumentViewer` are **C106's own
 * components**, imported rather than reimplemented — so "the document thumbnails on
 * a vehicle detail open the shared viewer" is true of the code and not only of the
 * screens. What is not shared is the *path*: `proxy.ts` gates a route on the screen
 * it resolves to, so the relay these images are fetched through is
 * `/vehicles/media/{docId}` and not `/verification/media/{docId}`. A Support CSR
 * holds the vehicle directory and not the queues, and routing a thumbnail through
 * the other screen's path would answer 403 on a screen they are permitted to open.
 *
 * Every tile is still one `DOC_VIEW` row, still not lazy-loaded, and still fetched
 * through the audited viewer — that reasoning belongs to `DocumentGrid` and none of
 * it changes because the record beside it is a vehicle.
 *
 * ## The two certificates are pills because the date alone says nothing
 *
 * An insurance certificate that lapses in a fortnight looks exactly like one that
 * lapses in a year, and the first is what an operator opened this record to find
 * (`expiryPill`). An **absent** expiry is `—` and not an expired one: the platform
 * holding no date is a different fact from the date having passed.
 *
 * ## Suspension is Moderation's, and this screen links to it
 *
 * `POST /v1/admin/vehicles/{id}/suspend` exists and is not called here: US-14.3 is
 * SCR-AP-004's decision, taken with a reason recorded against the moderator's name.
 * The wireframe says so — "suspend/delist actions route through Moderation" — and
 * the hand-off carries this vehicle's id into that card.
 */

export const dynamic = 'force-dynamic';

const TAB_LABELS: Readonly<Record<VehicleTab, AdminMessageKey>> = {
  trips: 'admin.directory.tab.trips',
  earnings: 'admin.directory.tab.earnings',
  dailyFee: 'admin.directory.tab.dailyFee',
  reports: 'admin.directory.tab.reports',
};

export default async function VehicleDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ vehicleId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { vehicleId } = await params;
  const query = await searchParams;

  if (!isDirectoryId(vehicleId)) notFound();

  const selection = vehicleSelection(query);
  const tab = tabSelection(VEHICLE_TABS, query);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  let detail: AdminVehicleDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<AdminVehicleDetail>({ path: vehiclePath(vehicleId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const back = vehiclesHref(selection);

  if (!detail) {
    return (
      <div className="flex flex-col gap-md">
        <DetailHeader
          backHref={back}
          backLabel={t('admin.directory.backToVehicles')}
          title={t('admin.directory.vehicle.unavailable')}
          subjectId={vehicleId}
        />
        {problem ? <ProblemPanel problem={problem} /> : null}
      </div>
    );
  }

  const { info } = detail;
  const menu = session?.menu ?? [];
  const reportsPath = menuPath(menu, 'reports');
  const verificationPath = menuPath(menu, 'verification');
  const driversPath = menuPath(menu, 'drivers');

  const handoffs: HandoffView[] = [
    ...(driversPath && info.ownerId && !info.fleetOrg
      ? [
          {
            key: 'owner',
            href: `${driversPath}/${info.ownerId}`,
            label: t('admin.directory.handoff.owner'),
            hint: t('admin.directory.handoff.ownerHint'),
          },
        ]
      : []),
    ...(verificationPath
      ? [
          {
            key: 'verification',
            href: `${verificationPath}/${vehicleId}`,
            label: t('admin.directory.handoff.registration'),
            hint: t('admin.directory.handoff.registrationHint'),
          },
        ]
      : []),
    ...(reportsPath
      ? [
          {
            key: 'suspend',
            href: `${reportsPath}?subject=vehicle&subjectId=${vehicleId}#suspend`,
            label: t('admin.directory.handoff.suspendVehicle'),
            hint: t('admin.directory.handoff.suspendHint'),
          },
        ]
      : []),
  ];

  const detailUrl = vehicleHref(selection, vehicleId);
  const suspended = dispatchStatePill(info.dispatchState, t);
  const panel = PANELS[tab](detail, context);

  const tiles = documentTiles(
    detail.documents ?? [],
    {
      viewer: (docId) => vehicleDocHref(selection, vehicleId, docId),
      media: (docId) => vehicleMediaHref(docId, 'thumb'),
    },
    context,
  );

  return (
    <div className="flex flex-col gap-md">
      <DetailHeader
        backHref={back}
        backLabel={t('admin.directory.backToVehicles')}
        title={info.regNo}
        subjectId={info.vehicleId}
        pill={vehicleHeadline(detail, t)}
      />

      {suspended ? (
        <p
          role="status"
          className="rounded-card border border-error/40 bg-error/10 p-sm text-body-sm text-on-surface"
        >
          {t('admin.directory.vehicle.dispatchSuspendedNote')}
        </p>
      ) : null}

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <ProfileCard
          heading={t('admin.directory.vehicle.profile')}
          facts={vehicleFacts(info, context)}
          note={t('admin.directory.piiNotice')}
        >
          <Handoffs heading={t('admin.directory.handoff.heading')} items={handoffs} />
        </ProfileCard>

        <div className="flex min-w-0 flex-1 flex-col gap-md">
          <DocumentGrid
            tiles={tiles}
            labels={{
              heading: t('admin.verification.doc.heading'),
              hint: t('admin.verification.doc.hint'),
              empty: t('admin.directory.vehicle.noDocuments'),
              note: t('admin.verification.doc.note'),
            }}
          />

          <ActivityPanel
            navLabel={t('admin.directory.tabs.label')}
            tabs={VEHICLE_TABS.map((id) => ({
              id,
              href: tabHref(detailUrl, id, VEHICLE_TABS[0]),
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
    </div>
  );
}

interface PanelView {
  readonly columns: readonly string[];
  readonly rows: readonly TableRowView[];
  readonly empty: string;
  readonly note?: string;
}

const PANELS: Readonly<
  Record<VehicleTab, (detail: AdminVehicleDetail, context: RenderContext) => PanelView>
> = {
  trips: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.journey'),
      context.t('admin.directory.field.regNo'),
      context.t('admin.directory.column.counterparty'),
      context.t('admin.directory.column.fare'),
      context.t('admin.directory.column.state'),
    ],
    rows: tripRows(detail.trips ?? [], context),
    empty: context.t('admin.directory.trip.empty'),
    note: context.t('admin.directory.trip.note'),
  }),
  earnings: (detail, context) => ({
    columns: [
      context.t('admin.directory.column.day'),
      context.t('admin.directory.column.trips'),
      context.t('admin.directory.column.gross'),
    ],
    rows: earningsRows(detail.earnings ?? [], context),
    empty: context.t('admin.directory.earnings.empty'),
    note: context.t('admin.directory.earnings.note'),
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
