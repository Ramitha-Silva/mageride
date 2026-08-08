import type { AuditEvent } from '@/api/audit-log';
import type { Role } from '@/api/types';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';
import { formatDateTime } from '@/i18n/format';

/**
 * SCR-AP-009's view model.
 *
 * ## The action is printed, not translated
 *
 * `WALLET_FEE_REVERSED` is an identifier in an immutable log, not copy. It is what
 * an auditor filters on (`?action=`), what `AdminAuditActions.cs` declares as a
 * constant so that "an action string that differs by one character between the
 * route that writes it and the screen that filters on it" cannot happen, and what
 * appears in every export and every ticket. Translating it would make the column
 * unsearchable and the three languages disagree about what happened.
 *
 * The **role** is translated, because a role is a job somebody holds and this
 * surface already has the nine names in three languages.
 *
 * ## `before` and `after` are summarised, never invented
 *
 * They are free-form JSON — whatever the handler knew about the entity — so the
 * row shows which of the two the event carries and the count of fields in each,
 * and the pair itself is rendered as the JSON it is. A screen that tried to
 * diff them would be guessing at the shape of every entity on the platform, and
 * the one thing an audit trail must not do is paraphrase.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

const ROLE_LABEL = {
  admin: 'admin.role.admin',
  super_admin: 'admin.role.super_admin',
  verification_officer: 'admin.role.verification_officer',
  support_csr: 'admin.role.support_csr',
  finance_officer: 'admin.role.finance_officer',
  auditor: 'admin.role.auditor',
  driver: 'admin.role.driver',
  passenger: 'admin.role.passenger',
  fleet_owner: 'admin.role.fleet_owner',
} as const satisfies Record<Role, AdminMessageKey>;

export interface AuditRowView {
  readonly key: string;
  readonly when: string | null;
  readonly actorId: string;
  readonly role: string | null;
  /** The `audit.events.action` verbatim. Never translated — see this file's note. */
  readonly action: string;
  readonly target: string | null;
  readonly targetType: string | null;
  readonly ip: string | null;
  /** The before/after pair as stored, for the operator who needs to see it. */
  readonly change: string | null;
}

function pretty(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  try {
    return JSON.stringify(value, null, 1);
  } catch {
    return null;
  }
}

export function auditRows(
  events: readonly AuditEvent[],
  { t, locale }: RenderContext,
): AuditRowView[] {
  return events.map((event) => {
    const before = pretty(event.before);
    const after = pretty(event.after);

    return {
      key: event.eventId,
      when: formatDateTime(locale, event.occurredAt),
      actorId: event.actorId,
      // An unknown role is left absent rather than guessed: the column is a fact
      // about who acted, and `PDPA_REQUESTED` rows carry `passenger` or `driver`
      // precisely because they are not an operator's decision.
      role: event.actorRole && event.actorRole in ROLE_LABEL
        ? t(ROLE_LABEL[event.actorRole])
        : (event.actorRole ?? null),
      action: event.action,
      target: event.subjectId ?? null,
      targetType: event.subjectType ?? null,
      ip: event.ip?.trim() ? event.ip.trim() : null,
      change:
        before || after
          ? [before ? `− ${before}` : null, after ? `+ ${after}` : null]
              .filter(Boolean)
              .join('\n')
          : null,
    };
  });
}

/**
 * The CSV the export writes, from the same rows the screen renders.
 *
 * **This portal does not normally render an export and here it does.** C105's rule
 * is that the bytes are relayed because admin-bff builds the file from the same
 * service call that answers the screen — "a portal that formatted its own CSV out
 * of the JSON would be a second implementation of the same document". There is no
 * first implementation to relay: `GET /v1/admin/audit-log` has no `.csv` sibling,
 * and US-19.3 asks for the export in as many words. So it is built here, from the
 * rows `auditSearch` fetched, and the C108 handoff asks admin-bff for the route
 * that would let this be deleted.
 *
 * Fields are quoted and inner quotes doubled (RFC 4180). Money does not appear;
 * ids, actions and instants do, and every one of them is written exactly as the
 * log holds it — an audit export that reformatted its own contents would not be
 * one.
 */
export function auditCsv(events: readonly AuditEvent[]): string {
  const header = [
    'eventId',
    'occurredAt',
    'actorId',
    'actorRole',
    'action',
    'subjectType',
    'subjectId',
    'ip',
    'before',
    'after',
  ];

  const rows = events.map((event) => [
    event.eventId,
    event.occurredAt,
    event.actorId,
    event.actorRole ?? '',
    event.action,
    event.subjectType ?? '',
    event.subjectId ?? '',
    event.ip ?? '',
    event.before === undefined || event.before === null ? '' : JSON.stringify(event.before),
    event.after === undefined || event.after === null ? '' : JSON.stringify(event.after),
  ]);

  return [header, ...rows].map((row) => row.map(quote).join(',')).join('\r\n');
}

function quote(value: string): string {
  return `"${value.replaceAll('"', '""')}"`;
}
