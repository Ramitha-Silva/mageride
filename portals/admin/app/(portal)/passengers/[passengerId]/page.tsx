import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import {
  isDirectoryId,
  passengerPath,
  passengerSelection,
  PASSENGER_TABS,
  tabSelection,
  type PassengerDetail,
  type PassengerTab,
} from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ActivityPanel } from '@/components/directories/ActivityPanel';
import { DetailHeader } from '@/components/directories/DetailHeader';
import { Handoffs, type HandoffView } from '@/components/directories/Handoffs';
import {
  disputeRows,
  packageRows,
  passengerFacts,
  passengerStatusPill,
  paymentRows,
  tripRows,
  type RenderContext,
  type TableRowView,
} from '@/components/directories/model';
import {
  menuPath,
  passengerHref,
  passengersHref,
  tabHref,
} from '@/components/directories/links';
import { ProfileCard } from '@/components/directories/ProfileCard';
import type { AdminMessageKey } from '@/i18n';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-011 · `passenger_detail`** — one passenger's profile and their Trips /
 * Payments / Packages / Disputes (AL-40, US-24.9, US-16.4).
 *
 * ## Opening this screen is the audited act
 *
 * `GET /v1/admin/passengers/{id}` is `.Audited(PII_READ, passenger)` — "exactly one
 * per open, carrying whether the contact details were actually revealed". The
 * portal does not write that row and must not: admin-bff's interceptor writes it
 * once the response is known to be a success, which is the only way the row and the
 * disclosure cannot disagree. What this side owes the operator is that the screen
 * says so, in their own language, while they are reading it.
 *
 * ## Read-only, and the two things it therefore does not do
 *
 * BR-28.8. There is no wallet control, no refund button and no way to raise a
 * ticket from here — support-svc has a create route and admin-bff exposes none, so
 * the wireframe's "Raise / link ticket" is a **link to the queue that owns it**
 * rather than a control that would post nothing. It is drawn only when the caller's
 * menu carries that queue (`Handoffs`).
 *
 * ## The tab is the URL
 *
 * All four arrays arrive on the one read, and only the tab being read is rendered —
 * so a client component holding the payload would ship every recipient's number and
 * every dispute to the browser so that three of them could be shown by a press. The
 * cost is stated in `api/directories.ts`: a second tab is a second read and a
 * second `PII_READ` row, which is what a second look at somebody's record is.
 */

export const dynamic = 'force-dynamic';

const TAB_LABELS: Readonly<Record<PassengerTab, AdminMessageKey>> = {
  trips: 'admin.directory.tab.trips',
  payments: 'admin.directory.tab.payments',
  packages: 'admin.directory.tab.packages',
  disputes: 'admin.directory.tab.disputes',
};

export default async function PassengerDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ passengerId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { passengerId } = await params;
  const query = await searchParams;

  if (!isDirectoryId(passengerId)) notFound();

  const selection = passengerSelection(query);
  const tab = tabSelection(PASSENGER_TABS, query);

  const [t, locale, session] = await Promise.all([getTranslator(), getLocale(), getSession()]);
  const context: RenderContext = { t, locale };

  let detail: PassengerDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<PassengerDetail>({ path: passengerPath(passengerId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const back = passengersHref(selection);

  if (!detail) {
    return (
      <div className="flex flex-col gap-md">
        <DetailHeader
          backHref={back}
          backLabel={t('admin.directory.backToPassengers')}
          title={t('admin.directory.passenger.unavailable')}
          subjectId={passengerId}
        />
        {problem ? <ProblemPanel problem={problem} /> : null}
      </div>
    );
  }

  const { profile } = detail;
  const detailHref = passengerHref(selection, passengerId);
  const supportPath = menuPath(session?.menu ?? [], 'support-tickets');

  const handoffs: HandoffView[] = supportPath
    ? [
        {
          key: 'tickets',
          href: supportPath,
          label: t('admin.directory.handoff.tickets'),
          hint: t('admin.directory.handoff.ticketsHint'),
        },
      ]
    : [];

  const panel = PANELS[tab](detail, supportPath ?? null, context);

  return (
    <div className="flex flex-col gap-md">
      <DetailHeader
        backHref={back}
        backLabel={t('admin.directory.backToPassengers')}
        title={profile.name}
        subjectId={profile.passengerId}
        pill={passengerStatusPill(profile.status, t)}
      />

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <ProfileCard
          heading={t('admin.directory.passenger.profile')}
          facts={passengerFacts(profile, context)}
          note={t('admin.directory.piiNotice')}
        >
          <Handoffs heading={t('admin.directory.handoff.heading')} items={handoffs} />
        </ProfileCard>

        <ActivityPanel
          navLabel={t('admin.directory.tabs.label')}
          tabs={PASSENGER_TABS.map((id) => ({
            id,
            href: tabHref(detailHref, id, PASSENGER_TABS[0]),
            label: t(TAB_LABELS[id]),
            current: id === tab,
          }))}
          rows={panel.rows}
          labels={{
            caption: t(TAB_LABELS[tab]),
            columns: panel.columns,
            empty: panel.empty,
            ...(tab === 'disputes' && supportPath
              ? { open: t('admin.directory.open') }
              : {}),
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

/**
 * One tab's table, built from the one payload the screen already read.
 *
 * A lookup rather than a `switch` so that adding the fifth tab the platform grows
 * is one entry and not an edit to the rendering path.
 */
const PANELS: Readonly<
  Record<PassengerTab, (detail: PassengerDetail, supportPath: string | null, context: RenderContext) => PanelView>
> = {
  trips: (detail, _support, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.journey'),
      context.t('admin.directory.field.regNo'),
      context.t('admin.directory.column.driver'),
      context.t('admin.directory.column.fare'),
      context.t('admin.directory.column.state'),
    ],
    rows: tripRows(detail.trips ?? [], context),
    empty: context.t('admin.directory.trip.empty'),
    note: context.t('admin.directory.trip.note'),
  }),
  payments: (detail, _support, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.method'),
      context.t('admin.directory.column.amount'),
      context.t('admin.directory.column.extras'),
      context.t('admin.directory.column.attempt'),
      context.t('admin.directory.column.state'),
    ],
    rows: paymentRows(detail.payments ?? [], context),
    empty: context.t('admin.directory.payment.empty'),
    note: context.t('admin.directory.payment.note'),
  }),
  packages: (detail, _support, context) => ({
    columns: [
      context.t('admin.directory.column.when'),
      context.t('admin.directory.column.package'),
      context.t('admin.directory.column.recipient'),
      context.t('admin.directory.column.fare'),
      context.t('admin.directory.column.delivered'),
      context.t('admin.directory.column.state'),
    ],
    rows: packageRows(detail.packages ?? [], context),
    empty: context.t('admin.directory.package.empty'),
  }),
  disputes: (detail, supportPath, context) => ({
    columns: [
      context.t('admin.directory.column.raised'),
      context.t('admin.directory.column.category'),
      context.t('admin.directory.column.description'),
      context.t('admin.directory.column.updated'),
      context.t('admin.directory.column.status'),
    ],
    rows: disputeRows(
      detail.disputes ?? [],
      supportPath ? (ticketId) => `${supportPath}?ticket=${ticketId}` : null,
      context,
    ),
    empty: context.t('admin.directory.dispute.empty'),
    note: context.t('admin.directory.dispute.note'),
  }),
};
