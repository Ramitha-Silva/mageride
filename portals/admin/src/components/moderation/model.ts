import type { StatusTone } from '@mageride/ui';

import {
  DELISTING_THRESHOLD,
  suspendHref,
  type CursorPage,
  type ReportRow,
} from '@/api/moderation';
import type { AdminTranslator, Locale } from '@/i18n';
import { formatCount, formatDateTime } from '@/i18n/format';

/**
 * SCR-AP-004's view model: the arithmetic and the vocabulary, apart from the
 * markup that draws them.
 *
 * Everything here is pure, which is what lets the two rules the screen is about be
 * asserted directly — that a count of *pending* reports is never printed as a
 * count of strikes, and that a report row offers the decision the platform will
 * actually take.
 *
 * ## The count in the wireframe's "Reports" column
 *
 * The sketch puts `3` against a subject, and three is the delisting number
 * (US-12.6) — but the figure this screen can honestly print is **how many pending
 * reports this vehicle has in the queue**, because `ReportRow.confirmedCount` is
 * `null` on every row admin-bff answers with (safety-svc's internal row does not
 * carry it, so there is nothing to forward). The confirmed total exists for
 * exactly one moment: the answer to a confirmation, which is why that number
 * reaches the operator on the banner after a decision and nowhere else.
 *
 * So the column counts what it says it counts, and the two facts are never mixed:
 * a vehicle with two pending reports may have none confirmed, and printing `2` as
 * "two strikes" would tell a moderator they were one press from delisting somebody
 * when they might be three.
 */

export interface PillView {
  readonly tone: StatusTone;
  readonly label: string;
}

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

export interface ReportRowView {
  readonly key: string;
  readonly reportId: string;
  /** The subject. A report names a **vehicle**; nothing on the row names its driver. */
  readonly vehicleId: string;
  /** How many pending reports this vehicle has on this page. */
  readonly pending: PillView;
  /** The reporter's own words (`safety.vehicle_reports.reason`, free text). */
  readonly reason: string | null;
  readonly raised: string | null;
  /** The suspend card, aimed at this vehicle. */
  readonly suspendHref: string;
  /**
   * Accessible names for the row's two controls.
   *
   * A queue of rows whose every button announces "Confirm" is a list a screen
   * reader cannot act on, and the visible label has to stay short — so the full
   * sentence is built here, where the translator and the row's subject both are.
   */
  readonly confirmNamed: string;
  readonly dismissNamed: string;
}

/**
 * A pending-report count, toned by how much of a pattern it is.
 *
 * Deliberately not toned by proximity to {@link DELISTING_THRESHOLD}: these are
 * reports nobody has upheld yet. What the colour says is "several people have
 * complained about this vehicle", which is a reason to look first, not a reason to
 * confirm.
 */
function pendingPill(count: number, { t, locale }: RenderContext): PillView {
  return {
    tone: count >= DELISTING_THRESHOLD ? 'error' : count > 1 ? 'warning' : 'neutral',
    label: t('admin.moderation.report.pendingCount', { count: formatCount(locale, count) }),
  };
}

export function reportRows(
  rows: readonly ReportRow[],
  context: RenderContext,
): ReportRowView[] {
  const perVehicle = new Map<string, number>();
  for (const row of rows) perVehicle.set(row.vehicleId, (perVehicle.get(row.vehicleId) ?? 0) + 1);

  return rows.map((row) => ({
    key: row.reportId,
    reportId: row.reportId,
    vehicleId: row.vehicleId,
    pending: pendingPill(perVehicle.get(row.vehicleId) ?? 1, context),
    reason: row.reason?.trim() ? row.reason.trim() : null,
    raised: formatDateTime(context.locale, row.createdAt),
    suspendHref: suspendHref('vehicle', row.vehicleId),
    confirmNamed: context.t('admin.moderation.report.confirmNamed', { vehicle: row.vehicleId }),
    dismissNamed: context.t('admin.moderation.report.dismissNamed', { vehicle: row.vehicleId }),
  }));
}

/**
 * The topbar figure: how many reports are waiting.
 *
 * Cursor pagination carries no total, so past one page this is `100+` rather than
 * a number nobody computed. A queue that failed reads `—`; the alternative is a
 * `0` that means "nothing is waiting" when it means "nobody answered".
 */
export function queueTotal(
  page: CursorPage<ReportRow> | null,
  { t, locale }: RenderContext,
): string {
  if (!page) return '—';

  const count = formatCount(locale, page.items.length);
  return t(page.hasMore ? 'admin.moderation.queue.totalMore' : 'admin.moderation.queue.total', {
    count,
  });
}

/**
 * What a just-recorded verdict left behind, as the sentence the queue greets the
 * moderator with.
 *
 * Three cases and they are genuinely different: a dismissal, a confirmation that
 * was not the third, and the confirmation that delisted the vehicle. The third is
 * the only place on this surface a strike total is a fact rather than a guess, so
 * it is the only place one is printed.
 */
export function verdictNotice(
  {
    decided,
    strikes,
    delisted,
  }: { decided?: 'CONFIRMED' | 'DISMISSED'; strikes?: number; delisted?: boolean },
  { t, locale }: RenderContext,
): { readonly tone: 'success' | 'error'; readonly message: string; readonly action: string } | null {
  if (!decided) return null;

  if (decided === 'DISMISSED') {
    return {
      tone: 'success',
      message: t('admin.moderation.verdict.dismissed'),
      action: 'REPORT_DISMISSED',
    };
  }

  const message = delisted
    ? t('admin.moderation.verdict.delisted', { count: formatCount(locale, DELISTING_THRESHOLD) })
    : typeof strikes === 'number'
      ? t('admin.moderation.verdict.confirmedCount', {
          count: formatCount(locale, strikes),
          remaining: formatCount(locale, Math.max(DELISTING_THRESHOLD - strikes, 0)),
        })
      : t('admin.moderation.verdict.confirmed');

  return { tone: delisted ? 'error' : 'success', message, action: 'REPORT_CONFIRMED' };
}
