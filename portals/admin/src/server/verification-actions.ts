'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  isSubjectId,
  subjectApprovePath,
  subjectFieldPath,
  subjectRejectPath,
  type FieldDecision,
  type VerificationDecision,
} from '@/api/verification';
import { getTranslator } from '@/i18n/server';

import { safeNextPath } from './next-path';

/**
 * SCR-AP-003a/003c's two decisions, as server actions.
 *
 * ## Confirm and Edit & confirm are one route, and the difference is one field
 *
 * `PUT /v1/admin/verification/{id}/fields/{key}` takes an optional `value`:
 * **omit it to confirm the extracted value as is, supply it to edit and confirm**.
 * They are not two operations with a shared implementation — they are one
 * decision the officer either agrees with or corrects, and C063 records the
 * difference where it matters: a corrected value becomes `source='manual'` with
 * no confidence, because an invented score would read as evidence.
 *
 * ## Nothing here writes an audit row, and nothing here may
 *
 * Every call declares its D-35 row through {@link mutate}'s required
 * `AuditIntent`, and admin-bff's interceptor writes it inside the same
 * transaction as the change. A second row posted from this side would be an entry
 * in an immutable log claiming an action whose transaction may have rolled back.
 *
 * ## Failures come back as a sentence, not as a thrown error
 *
 * A `409` on Approve is the platform saying "a field is still pending" — the one
 * thing the officer most needs to read, and an error boundary would take the
 * screen away to say it. So the shape is `SignInState`'s: the action resolves the
 * message in the operator's language and returns it, and the form that called it
 * renders it beside the control that caused it.
 */

export interface FieldDecisionState {
  /** The failure, already translated. Absent on success. */
  readonly message?: string;
}

export interface SubjectDecisionState {
  readonly message?: string;
  /** Set when the failure is about the reason box rather than the call. */
  readonly field?: 'reason';
}

/**
 * Where a decision returns to.
 *
 * `safeNextPath` refuses an absolute or scheme-relative URL — a redirect
 * instruction that arrives in a form field arrives from whoever wrote the page it
 * was posted from. The extra `/verification` test narrows it further: these
 * actions belong to one screen, so the only honest destination is inside it.
 */
function returnPath(value: FormDataEntryValue | null, fallback: string): string {
  const path = safeNextPath(typeof value === 'string' ? value : null);
  if (!path) return fallback;

  // `/verification`, or something under it. Spelled out rather than as a prefix
  // test, so `/verification-something` is not this screen by accident.
  const [pathname] = path.split('?');
  return pathname === '/verification' || pathname?.startsWith('/verification/') ? path : fallback;
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

/** The `audit.events.entity_type` a subject's decisions are recorded against. */
function entityFor(subjectType: string): 'driver' | 'vehicle' | 'fleet_org' {
  if (subjectType === 'vehicle') return 'vehicle';
  if (subjectType === 'org') return 'fleet_org';
  return 'driver';
}

/**
 * Confirm a flagged field, or correct it and confirm it (US-2.4a/2.10a).
 *
 * `intent=edit` is what makes the value part of the request: the officer typed
 * something, so it is theirs. `intent=confirm` sends no body value at all even if
 * the box has one in it, because a bare confirm means "the extraction was right"
 * and a value smuggled onto it would rewrite the field as a manual entry nobody
 * asked for.
 */
export async function decideField(
  _state: FieldDecisionState,
  formData: FormData,
): Promise<FieldDecisionState> {
  const t = await getTranslator();

  const subjectId = text(formData, 'subjectId');
  const fieldKey = text(formData, 'fieldKey');
  const subjectType = text(formData, 'subjectType');
  const editing = text(formData, 'intent') === 'edit';
  const value = text(formData, 'value');
  const path = returnPath(formData.get('returnTo'), '/verification');

  if (!isSubjectId(subjectId) || !fieldKey) {
    return { message: t('admin.error.unexpected') };
  }

  if (editing && !value) {
    return { message: t('admin.verification.field.valueRequired') };
  }

  try {
    await mutate<FieldDecision, { value?: string }>({
      method: 'PUT',
      path: subjectFieldPath(subjectId, fieldKey),
      body: editing ? { value } : {},
      audit: {
        action: 'VERIFICATION_FIELD_CONFIRMED',
        entity: entityFor(subjectType),
        entityId: subjectId,
      },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  // The rail's Approve gate is recomputed from the same read the fields are, so
  // the page has to be re-rendered rather than patched: confirming the last
  // pending field is exactly the moment the button unlocks.
  revalidatePath(path);
  return {};
}

/**
 * Approve or reject a driver, a vehicle or a fleet organisation.
 *
 * One action and one form, because the wireframe draws one decision panel with
 * two buttons and the two share the reason box. Which button was pressed arrives
 * as `intent`.
 *
 * **Approve is not sent while a field is unconfirmed.** The button is disabled and
 * this refuses it again, because a disabled button is a statement about a
 * rendered page and the page may be a minute old — the officer could have opened
 * a second tab, or the platform could have flagged a renewal since. admin-bff
 * refuses it a third time with a `409`, which is the authority; the two here are
 * about not asking.
 */
export async function decideSubject(
  _state: SubjectDecisionState,
  formData: FormData,
): Promise<SubjectDecisionState> {
  const t = await getTranslator();

  const subjectId = text(formData, 'subjectId');
  const subjectType = text(formData, 'subjectType');
  const rejecting = text(formData, 'intent') === 'reject';
  const reason = text(formData, 'reason');
  const approvable = text(formData, 'approvable') === 'true';
  const path = returnPath(formData.get('returnTo'), '/verification');

  if (!isSubjectId(subjectId)) return { message: t('admin.error.unexpected') };

  // US-2.15: the reason is mandatory and is shown to the applicant verbatim.
  // "Rejected" with nothing to read leaves somebody unable to fix the one thing
  // between them and driving.
  if (rejecting && !reason) {
    return { message: t('admin.verification.reject.reasonRequired'), field: 'reason' };
  }

  if (!rejecting && !approvable) {
    return { message: t('admin.verification.approve.blocked') };
  }

  const entity = entityFor(subjectType);

  try {
    await mutate<VerificationDecision, { reason: string }>({
      method: 'POST',
      path: rejecting ? subjectRejectPath(subjectId) : subjectApprovePath(subjectId),
      ...(rejecting ? { body: { reason } } : {}),
      audit: {
        action: rejecting ? 'VERIFICATION_REJECTED' : 'VERIFICATION_APPROVED',
        entity,
        entityId: subjectId,
      },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  // A decided subject is no longer a queue entry, so the screen it belongs on is
  // the queue. `decided` is what lets that screen say which row the operator's
  // name has just gone onto — staying here would show the same page back, since
  // a verdict is not part of the detail payload.
  redirect(`${path}${path.includes('?') ? '&' : '?'}decided=${rejecting ? 'rejected' : 'approved'}`);
}
