'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';

import { mutate } from '@/api/client';
import {
  isAdminId,
  isSuspendSubject,
  reportResolvePath,
  suspendPath,
  type ModerationResult,
  type ResolvedReport,
} from '@/api/moderation';
import { ProblemError } from '@/api/problem';
import { getTranslator } from '@/i18n/server';

/**
 * SCR-AP-004's two decisions, as server actions: resolving a vehicle report
 * (US-12.6) and suspending a driver or a vehicle (US-14.3).
 *
 * ## Neither writes an audit row, and neither may
 *
 * Both declare their D-35 row through {@link mutate}'s required `AuditIntent`, and
 * admin-bff's interceptor writes it inside the same transaction as the change —
 * it refuses to start if a mutating route could avoid it, and throws on a mutating
 * 2xx that recorded nothing. A row posted from this side would be an entry in an
 * immutable log claiming an action whose transaction may have rolled back.
 *
 * ## The two differ in where they leave the operator, because the queue does
 *
 * A resolved report is no longer pending, so its row leaves the queue and the
 * screen has to say what happened to it — including **whether that confirmation
 * was the third**, which is the only moment the platform tells anyone a vehicle's
 * strike total. A suspension changes nothing on the queue, so it answers in place,
 * under the button that caused it.
 */

export interface ReportDecisionState {
  /** The failure, already translated. Absent on success. */
  readonly message?: string;
}

export interface SuspendState {
  readonly message?: string;
  /** Set when the failure is about a box rather than about the call. */
  readonly field?: 'subjectId' | 'reason';
  /** The subject that was suspended, so the form can confirm it in place. */
  readonly suspended?: {
    readonly subject: string;
    readonly subjectId: string;
  };
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * Confirm or dismiss a vehicle report (US-12.6).
 *
 * **Nothing here delists anything.** The third confirmation is what removes the
 * vehicle, decided inside safety-svc's own transaction so that two moderators
 * confirming at the same instant cannot both read two — this action confirms *one
 * report* and reports back what that turned out to mean. Which is why the count
 * travels on the redirect rather than being recomputed: it is the number the
 * platform committed, not one this process derived.
 */
export async function decideReport(
  _state: ReportDecisionState,
  formData: FormData,
): Promise<ReportDecisionState> {
  const t = await getTranslator();

  const reportId = text(formData, 'reportId');
  const confirming = text(formData, 'intent') === 'confirm';
  const note = text(formData, 'note');

  if (!isAdminId(reportId)) return { message: t('admin.error.unexpected') };

  let outcome: ResolvedReport;

  try {
    ({ data: outcome } = await mutate<ResolvedReport, { decision: string; note?: string }>({
      method: 'POST',
      path: reportResolvePath(reportId),
      body: { decision: confirming ? 'CONFIRMED' : 'DISMISSED', ...(note ? { note } : {}) },
      audit: {
        // Two actions, not one `REPORT_RESOLVED`: "how many reports were upheld
        // last month" is a question an auditor answers with a filter, and
        // admin-bff records the pair for exactly that reason.
        action: confirming ? 'REPORT_CONFIRMED' : 'REPORT_DISMISSED',
        entity: 'vehicle_report',
        entityId: reportId,
      },
    }));
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  const query = new URLSearchParams({ decided: confirming ? 'CONFIRMED' : 'DISMISSED' });
  if (typeof outcome.confirmedCount === 'number') {
    query.set('strikes', String(outcome.confirmedCount));
  }
  if (outcome.vehicleDelisted) query.set('delisted', 'true');

  redirect(`/reports?${query.toString()}`);
}

/**
 * Suspend a driver or a vehicle, with the reason that is recorded against it
 * (US-14.3).
 *
 * **The reason is required here and required again in admin-bff**, which answers
 * `400` without one. Its own comment says why: "a suspension with no recorded
 * reason is one nobody can appeal and nobody can explain". This copy exists so an
 * operator is told that before the request, in their own language.
 *
 * **The id is checked for shape before it is put in a path.** This is the one
 * control on the screen that takes an identifier from a person rather than from a
 * row, and a mistyped id must fail as a mistyped id rather than as a 404 from a
 * URL this process built out of whatever was in the box.
 */
export async function suspendSubject(
  _state: SuspendState,
  formData: FormData,
): Promise<SuspendState> {
  const t = await getTranslator();

  const subject = text(formData, 'subject');
  const subjectId = text(formData, 'subjectId');
  const reason = text(formData, 'reason');

  if (!isSuspendSubject(subject)) return { message: t('admin.error.unexpected') };

  if (!isAdminId(subjectId)) {
    return { message: t('admin.moderation.suspend.idRequired'), field: 'subjectId' };
  }

  if (!reason) {
    return { message: t('admin.moderation.suspend.reasonRequired'), field: 'reason' };
  }

  try {
    await mutate<ModerationResult, { reason: string }>({
      method: 'POST',
      path: suspendPath(subject, subjectId),
      body: { reason },
      audit: {
        action: subject === 'vehicle' ? 'VEHICLE_SUSPENDED' : 'DRIVER_SUSPENDED',
        entity: subject,
        entityId: subjectId,
      },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  // A suspension is idempotent and admin-bff answers 200 either way, so there is
  // nothing here to redirect away from — but the queue beside the card may now be
  // reading a vehicle that has left dispatch, and the operator should see this
  // page as it now is.
  revalidatePath('/reports');

  return { suspended: { subject, subjectId } };
}
