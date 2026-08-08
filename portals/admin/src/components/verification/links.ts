import { queueQuery, queueSelection } from '@/api/verification';
import type { AdminMessageKey } from '@/i18n';

/**
 * The URLs SCR-AP-003's four screens navigate between.
 *
 * They are in one module because the officer's **filter has to survive the round
 * trip**: open a row from the fleet-org tab with a plate searched, decide, and
 * come back to that tab with that search still in the box. Every link on the
 * detail, the lightbox and the org screen therefore carries the queue's own query
 * onward, and one place that builds it is what stops three of them agreeing and a
 * fourth quietly dropping the search.
 */

/** The queue's tab, search and status, normalised, as a query string. */
export function queryString(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): string {
  return new URLSearchParams(queueQuery(queueSelection(params))).toString();
}

/** Back to the queue the officer came from. */
export function backHref(search: string): string {
  return search ? `/verification?${search}` : '/verification';
}

/** The portal's relay of an audited document rendition (SCR-AP-003b). */
export function mediaHref(docId: string, variant: 'thumb' | 'full'): string {
  return `/verification/media/${docId}?variant=${variant}`;
}

/**
 * What the Approve button says.
 *
 * Named per subject, which is SCR-AP-003c's own wording ("Approve organisation")
 * carried across the family. The wireframe's SCR-AP-003a label — "Confirm all &
 * approve" — is the one deviation this screen makes: the button confirms nothing,
 * it posts the approval, and while a field is pending it is disabled for exactly
 * the reason its label would have promised to fix.
 */
export function approveLabelKey(subjectType: string): AdminMessageKey {
  if (subjectType === 'vehicle') return 'admin.verification.decision.approveVehicle';
  if (subjectType === 'org') return 'admin.verification.decision.approveOrg';
  return 'admin.verification.decision.approveDriver';
}
