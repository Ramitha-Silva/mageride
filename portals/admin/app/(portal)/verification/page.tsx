import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  QUEUE_PATHS,
  queueQuery,
  queueSearch,
  queueSelection,
  type CursorPage,
  type DriverQueueRow,
  type OrgQueueRow,
  type QueueSelection,
  type VehicleQueueRow,
  type VerificationQueue,
} from '@/api/verification';
import { ProblemPanel } from '@/components/ProblemPanel';
import {
  driverQueueRows,
  orgQueueRows,
  queueCount,
  vehicleQueueRows,
  type QueueRowView,
  type RenderContext,
} from '@/components/verification/model';
import { QueueFilter } from '@/components/verification/QueueFilter';
import { QueueTable } from '@/components/verification/QueueTable';
import { QueueTabs } from '@/components/verification/QueueTabs';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-003 · `verification_queues`** — the Verification Officer's three
 * pending queues (AL-39, US-24.8).
 *
 * ## What a queue is
 *
 * "Has a `registry.document_fields` row still `pending`." That is AL-27's fence
 * stated as the query it is, it lives in admin-bff (C063), and it is why this
 * screen has no "only pending" filter to apply: an auto-verified document
 * produces no such row and therefore **cannot** appear, rather than being filtered
 * out by code that could later stop filtering. The status column is the
 * *subject's* own registration status, which is what D2's status filter filters
 * on — a renewal flagged on somebody already approved reads `Approved`, and a
 * resubmission after a refusal reads `Rejected`.
 *
 * ## Why all three queues are read on every render
 *
 * The wireframe puts a count on each tab and their sum in the topbar, and cursor
 * pagination carries no total — so the only honest figure is the number of rows a
 * queue answers. Reading the other two costs two calls and buys the officer the
 * one thing the three badges are for: where the work is, right now, under the
 * search they just typed. One search and one status filter reach all three, which
 * is also what makes the badges comparable.
 *
 * **A queue that fails does not take the screen with it.** The fleet-org tab is
 * fleet-svc's data behind admin-bff's route; when that upstream is down its badge
 * is `—` and its tab shows the problem, while the two queues that answered are
 * still workable.
 */

export const dynamic = 'force-dynamic';

interface QueueResult<T> {
  readonly page: CursorPage<T> | null;
  readonly problem: ProblemDetails | null;
}

async function loadQueue<T>(
  path: string,
  searchParams: Record<string, string | number>,
): Promise<QueueResult<T>> {
  try {
    return { page: await read<CursorPage<T>>({ path, searchParams }), problem: null };
  } catch (error) {
    // A refusal or an unreachable upstream is this tab's news, not the page's.
    // Anything that is not a problem+json failure is the error boundary's.
    if (!(error instanceof ProblemError)) throw error;
    return { page: null, problem: error.problem };
  }
}

export default async function VerificationQueuesPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = queueSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  const query = queueSearch(selection);
  const [licences, vehicles, orgs] = await Promise.all([
    loadQueue<DriverQueueRow>(QUEUE_PATHS['driving-license'], query),
    loadQueue<VehicleQueueRow>(QUEUE_PATHS['vehicle-registration'], query),
    loadQueue<OrgQueueRow>(QUEUE_PATHS['fleet-org'], query),
  ]);

  const back = new URLSearchParams(queueQuery(selection)).toString();
  const subjectHref = (id: string) => `/verification/${id}?${back}`;
  const orgHref = (id: string) => `/verification/org/${id}?${back}`;

  const active: QueueResult<unknown> =
    selection.queue === 'driving-license'
      ? licences
      : selection.queue === 'vehicle-registration'
        ? vehicles
        : orgs;

  const rows: QueueRowView[] =
    selection.queue === 'driving-license'
      ? driverQueueRows(licences.page?.items ?? [], subjectHref, context)
      : selection.queue === 'vehicle-registration'
        ? vehicleQueueRows(vehicles.page?.items ?? [], subjectHref, context)
        : orgQueueRows(orgs.page?.items ?? [], orgHref, context);

  const counts: Record<VerificationQueue, string> = {
    'driving-license': queueCount(licences.page, context),
    'vehicle-registration': queueCount(vehicles.page, context),
    'fleet-org': queueCount(orgs.page, context),
  };

  const answered = [licences, vehicles, orgs].filter((result) => result.page);
  const waiting = answered.reduce((total, result) => total + (result.page?.items.length ?? 0), 0);
  const capped = answered.some((result) => result.page?.hasMore);

  const decided = decision(params);

  return (
    <div className="flex flex-col gap-md">
      {decided ? (
        <p
          role="status"
          className="rounded-card border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {t(
            decided === 'approved'
              ? 'admin.verification.decided.approved'
              : 'admin.verification.decided.rejected',
          )}{' '}
          {t('admin.audit.recorded', {
            action: decided === 'approved' ? 'VERIFICATION_APPROVED' : 'VERIFICATION_REJECTED',
          })}
        </p>
      ) : null}

      <QueueFilter
        selection={selection}
        filtered={Boolean(selection.search || selection.status)}
        labels={{
          search: t('admin.verification.queue.search'),
          searchHint: t('admin.verification.queue.searchHint'),
          status: t('admin.verification.queue.status'),
          statusAll: t('admin.verification.queue.statusAll'),
          statusPending: t('status.pending'),
          statusApproved: t('status.approved'),
          statusRejected: t('status.rejected'),
          apply: t('admin.verification.queue.apply'),
          clear: t('admin.verification.queue.clear'),
          total: t(
            capped ? 'admin.verification.queue.totalMore' : 'admin.verification.queue.total',
            { count: waiting },
          ),
        }}
      />

      <QueueTabs
        selection={selection}
        counts={counts}
        labels={{
          navLabel: t('admin.verification.queue.navLabel'),
          drivingLicence: t('admin.verification.queue.drivingLicence'),
          vehicleRegistration: t('admin.verification.queue.vehicleRegistration'),
          fleetOrg: t('admin.verification.queue.fleetOrg'),
        }}
      />

      {active.problem ? <ProblemPanel problem={active.problem} /> : null}

      {active.page ? (
        <>
          <QueueTable rows={rows} labels={tableLabels(selection, t)} />

          {active.page.hasMore ? (
            <p className="text-caption text-on-surface-variant">
              {t('admin.verification.queue.capped', { count: active.page.items.length })}
            </p>
          ) : null}
        </>
      ) : null}
    </div>
  );
}

/** `?decided=` — what the officer's last verdict was, so the queue can confirm it. */
function decision(
  params: Readonly<Record<string, string | string[] | undefined>>,
): 'approved' | 'rejected' | null {
  const value = Array.isArray(params.decided) ? params.decided[0] : params.decided;
  return value === 'approved' || value === 'rejected' ? value : null;
}

/**
 * The active tab's headings and its two middle columns.
 *
 * The wireframe writes a queue's heading out in full ("Driving-licence
 * verifications — pending") and puts the reason for membership beside it. Both
 * are per-tab, and so is what the middle of a row carries.
 */
function tableLabels(selection: QueueSelection, t: Awaited<ReturnType<typeof getTranslator>>) {
  const org = selection.queue === 'fleet-org';

  return {
    heading: t(
      selection.queue === 'driving-license'
        ? 'admin.verification.queue.headingDrivingLicence'
        : selection.queue === 'vehicle-registration'
          ? 'admin.verification.queue.headingVehicleRegistration'
          : 'admin.verification.queue.headingFleetOrg',
    ),
    caption: t('admin.verification.queue.caption'),
    flagsOnly: t(
      org ? 'admin.verification.queue.orgGate' : 'admin.verification.queue.flagsOnly',
    ),
    subject: t(
      selection.queue === 'driving-license'
        ? 'admin.verification.column.driver'
        : selection.queue === 'vehicle-registration'
          ? 'admin.verification.column.vehicle'
          : 'admin.verification.column.organisation',
    ),
    middleOne: t(
      org ? 'admin.verification.column.vehicles' : 'admin.verification.column.submitted',
    ),
    middleTwo: t(
      org ? 'admin.verification.column.evidence' : 'admin.verification.column.flagged',
    ),
    status: t('admin.verification.column.status'),
    action: t('admin.verification.column.action'),
    review: t('admin.verification.queue.review'),
    empty: t('admin.verification.queue.empty'),
  };
}
