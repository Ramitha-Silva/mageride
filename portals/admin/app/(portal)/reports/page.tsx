import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import {
  DELISTING_THRESHOLD,
  moderationSelection,
  QUEUE_PAGE_SIZE,
  REPORT_QUEUE_PATH,
  type CursorPage,
  type ReportRow,
} from '@/api/moderation';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { queueTotal, reportRows, verdictNotice, type RenderContext } from '@/components/moderation/model';
import { ReportQueueTable } from '@/components/moderation/ReportQueueTable';
import { SuspendCard } from '@/components/moderation/SuspendCard';
import { ProblemPanel } from '@/components/ProblemPanel';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-004 · `moderation`** — the vehicle-report review queue and the
 * suspend / ban card (US-12.6, US-14.3, D-35).
 *
 * The nav item is `reports` and its path is `/reports`, which is what
 * `AdminMenu.cs` gives it; the wireframe's `/moderation` is the group, and the
 * group's other member is C065's fraud-review queue.
 *
 * ## The queue is not filtered here, because the queue *is* the filter
 *
 * Membership is "a `safety.vehicle_reports` row is still `PENDING`" — decided in
 * safety-svc, forwarded by admin-bff — so a decided report leaves because it is
 * no longer pending and not because this screen stopped drawing it. There is no
 * status control and there must not be one: it would be a filter over a set that
 * has already been filtered, and the first thing it could do wrong is show a
 * moderator somebody else's closed decision as work.
 *
 * ## Three confirmations delist, and this screen never says how many there are
 *
 * US-12.6's rule is stated on the card and counted by safety-svc inside the
 * transaction that writes the third status. The only figure the platform hands
 * anybody is the one on the answer to a confirmation, which is why the banner
 * after a verdict is the one place a strike total appears — see
 * `components/moderation/model.ts`.
 *
 * ## The suspend card is aimed through the URL
 *
 * "Suspend this vehicle" on a queue row is a `<Link>` to `?subject=vehicle&
 * subjectId=…#suspend`: no JavaScript, survives a reload, and the operator can
 * read in the address bar which subject the form is pointed at before pressing a
 * button that ends somebody's shift. The same reason SCR-AP-002's filter and
 * SCR-AP-003's tabs are URLs.
 */

export const dynamic = 'force-dynamic';

export default async function ModerationPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = moderationSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<ReportRow> | null = null;
  let problem: ProblemDetails | null = null;

  try {
    page = await read<CursorPage<ReportRow>>({
      path: REPORT_QUEUE_PATH,
      searchParams: { limit: QUEUE_PAGE_SIZE },
    });
  } catch (error) {
    // safety-svc behind admin-bff: an unreachable upstream is this queue's news.
    // The suspend card below does not depend on it and stays usable, which is the
    // point of not letting one failed read take the screen.
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const rows = reportRows(page?.items ?? [], context);
  const verdict = verdictNotice(selection, context);

  return (
    <div className="flex flex-col gap-md">
      {verdict ? (
        <p
          role="status"
          className={`rounded-card border p-sm text-body-sm text-on-surface ${
            verdict.tone === 'error' ? 'border-error/40 bg-error/10' : 'border-success/40 bg-success/10'
          }`}
        >
          {verdict.message} {t('admin.audit.recorded', { action: verdict.action })}
        </p>
      ) : null}

      <div className="flex flex-wrap items-center gap-xs">
        <StatusPill tone="warning">{queueTotal(page, context)}</StatusPill>
        <span className="text-caption text-on-surface-variant">
          {t('admin.moderation.queue.scope')}
        </span>
      </div>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {page ? (
        <>
          <ReportQueueTable
            rows={rows}
            labels={{
              heading: t('admin.moderation.queue.heading'),
              caption: t('admin.moderation.queue.caption'),
              rule: t('admin.moderation.queue.rule', { count: DELISTING_THRESHOLD }),
              subject: t('admin.moderation.column.subject'),
              reports: t('admin.moderation.column.reports'),
              reason: t('admin.moderation.column.reason'),
              raised: t('admin.moderation.column.raised'),
              action: t('admin.moderation.column.action'),
              noReason: t('admin.moderation.report.noReason'),
              suspend: t('admin.moderation.report.suspendVehicle'),
              empty: t('admin.moderation.queue.empty'),
              decision: {
                confirm: t('admin.moderation.report.confirm'),
                dismiss: t('admin.moderation.report.dismiss'),
                working: t('admin.moderation.report.working'),
              },
            }}
          />

          {page.hasMore ? (
            <p className="text-caption text-on-surface-variant">
              {t('admin.moderation.queue.capped', { count: page.items.length })}
            </p>
          ) : null}
        </>
      ) : null}

      <SuspendCard
        subject={selection.subject ?? 'driver'}
        subjectId={selection.subjectId ?? ''}
        labels={{
          heading: t('admin.moderation.suspend.heading'),
          subject: t('admin.moderation.suspend.subject'),
          driver: t('admin.moderation.suspend.driver'),
          vehicle: t('admin.moderation.suspend.vehicle'),
          subjectId: t('admin.moderation.suspend.subjectId'),
          subjectIdHint: t('admin.moderation.suspend.subjectIdHint'),
          reason: t('admin.moderation.suspend.reason'),
          reasonHint: t('admin.moderation.suspend.reasonHint'),
          apply: t('admin.moderation.suspend.apply'),
          working: t('admin.moderation.suspend.working'),
          noDuration: t('admin.moderation.suspend.noDuration'),
          audit: t('admin.audit.notice'),
          suspendedDriver: t('admin.moderation.suspend.doneDriver'),
          suspendedVehicle: t('admin.moderation.suspend.doneVehicle'),
          recordedDriver: t('admin.audit.recorded', { action: 'DRIVER_SUSPENDED' }),
          recordedVehicle: t('admin.audit.recorded', { action: 'VEHICLE_SUSPENDED' }),
        }}
      />
    </div>
  );
}
