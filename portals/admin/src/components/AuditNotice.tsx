import type { AuditIntent } from '@/api/audit';
import { getTranslator } from '@/i18n/server';

/**
 * The D-35 affordance: what to put beside a button before it is pressed, and what
 * to say after.
 *
 * Every admin-bff mutation writes an `audit.events` row against the caller's name,
 * and the interceptor *throws* on a mutating 2xx that recorded nothing — so on
 * this surface "was this written down" has one answer and it is yes. The operator
 * is entitled to know that before they act, not to discover it in a review; URD
 * §2.2 puts the immutable admin-action log in front of Auditors and Super Admins,
 * and a console that hid it from the person generating the rows would be the only
 * party to the arrangement kept in the dark.
 *
 * `action` names the row so the notice can be specific rather than a general
 * disclaimer nobody reads. The value comes from {@link AuditIntent}, which
 * `mutate()` requires — so a screen physically cannot render this notice for a
 * call whose audit row it has not declared.
 */
export async function AuditNotice({
  intent,
  state = 'pending',
}: {
  intent: AuditIntent;
  /** `pending` before the action, `recorded` after it succeeded. */
  state?: 'pending' | 'recorded';
}) {
  const t = await getTranslator();

  return (
    <p className="text-caption text-on-surface-variant">
      {state === 'pending' ? t('admin.audit.notice') : t('admin.audit.recorded', { action: intent.action })}
    </p>
  );
}
