import Link from 'next/link';

import { Button, Field, Input, StatusPill } from '@mageride/ui';

import {
  AUDIT_EXPORT_MAX_PAGES,
  AUDIT_LOG_PATH,
  AUDIT_PAGE_SIZE,
  auditHref,
  auditSearch,
  auditSelection,
  isFiltered,
  type AuditEvent,
  type CursorPage,
} from '@/api/audit-log';
import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { auditRows, type RenderContext } from '@/components/audit/model';
import { AuditTable } from '@/components/audit/AuditTable';
import { ProblemPanel } from '@/components/ProblemPanel';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-009 · `audit_logs`** — the immutable admin-action and permission-change
 * trail (US-19.3, D-35).
 *
 * ## Read-only, and not because this screen chose to be
 *
 * `GET /v1/admin/audit-log` has no write sibling anywhere on the platform:
 * "append-only — there is no write route here". The DoD item "the audit view is
 * read-only for the Auditor role with no mutating control rendered" is therefore
 * satisfied for **every** role, and `test/audit-screen.test.tsx` asserts it against
 * the tree rather than trusting the sentence.
 *
 * The Auditor's own cell is `✅ read` on the Audit-trail row, which is what puts
 * this item in their menu at all; Admin, Super Admin and Finance hold `👁`.
 *
 * ## The filters are the URL, and so is the page
 *
 * Four filters (`actorId`, `action`, `subjectId`, `from`/`to`) and one cursor, all
 * in a `method="get"` form and a pair of `<Link>`s. An auditor's whole job is
 * producing a view somebody else can reproduce, so a filter held in component
 * state would be the one thing this screen cannot afford: it would not survive a
 * reload, a bookmark, or a paste into the report the query was run for.
 *
 * `from`/`to` are **instants**, not business dates — the only date filter on this
 * surface that is — because a window of admin actions is a window on a clock.
 *
 * ## The export is rendered here, and that is a deviation
 *
 * SCR-AP-002's CSV relays bytes admin-bff produced. This one is built in the
 * portal, because `/v1/admin/audit-log` has no `.csv` sibling to relay and US-19.3
 * asks for the export. It follows the cursor to a **stated** maximum and says so
 * beside the link and inside the file. See the route handler, and the C108 handoff
 * for the micro-change-set that would let it be deleted.
 */

export const dynamic = 'force-dynamic';

export default async function AuditLogPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = auditSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  let page: CursorPage<AuditEvent> | null = null;
  let problem: ProblemDetails | null = null;

  try {
    page = await read<CursorPage<AuditEvent>>({
      path: AUDIT_LOG_PATH,
      searchParams: auditSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  const filtered = isFiltered(selection);

  return (
    <div className="flex flex-col gap-md">
      <form
        method="get"
        action="/audit-log"
        className="flex flex-wrap items-end gap-sm rounded-card border border-outline bg-background p-sm shadow-card"
      >
        <Field
          label={t('admin.audit.filter.actor')}
          hint={t('admin.audit.filter.idHint')}
          className="min-w-[260px] flex-1"
          {...(selection.typedActor && !selection.actorId
            ? { error: t('admin.audit.filter.idInvalid') }
            : {})}
        >
          <Input
            name="actorId"
            defaultValue={selection.typedActor}
            maxLength={40}
            autoCapitalize="none"
            spellCheck={false}
          />
        </Field>

        <Field
          label={t('admin.audit.filter.action')}
          hint={t('admin.audit.filter.actionHint')}
          className="w-[240px]"
        >
          <Input
            name="action"
            defaultValue={selection.action ?? ''}
            maxLength={60}
            autoCapitalize="characters"
            spellCheck={false}
          />
        </Field>

        <Field
          label={t('admin.audit.filter.subject')}
          hint={t('admin.audit.filter.idHint')}
          className="min-w-[260px] flex-1"
          {...(selection.typedSubject && !selection.subjectId
            ? { error: t('admin.audit.filter.idInvalid') }
            : {})}
        >
          <Input
            name="subjectId"
            defaultValue={selection.typedSubject}
            maxLength={40}
            autoCapitalize="none"
            spellCheck={false}
          />
        </Field>

        <Field label={t('admin.audit.filter.from')} className="w-[220px]">
          <Input type="datetime-local" name="from" defaultValue={localValue(selection.from)} />
        </Field>

        <Field
          label={t('admin.audit.filter.to')}
          hint={t('admin.audit.filter.timezone')}
          className="w-[220px]"
        >
          <Input type="datetime-local" name="to" defaultValue={localValue(selection.to)} />
        </Field>

        <Button type="submit" size="compact">
          {t('admin.audit.filter.apply')}
        </Button>

        {filtered ? (
          <Link
            href="/audit-log"
            className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
          >
            {t('admin.audit.filter.clear')}
          </Link>
        ) : null}
      </form>

      <div className="flex flex-wrap items-center gap-sm">
        <h1 className="text-subtitle font-semibold">{t('admin.audit.heading')}</h1>
        <StatusPill tone="neutral">{t('admin.audit.readOnly')}</StatusPill>
        <span className="flex-1" />
        <Link
          href={auditHref('/audit-log/export', selection, { cursor: null })}
          className="inline-flex h-10 items-center rounded-sm border border-outline px-md text-body-sm text-on-surface hover:bg-surface-variant"
        >
          {t('admin.audit.export')}
        </Link>
      </div>

      <p className="text-caption text-on-surface-variant">
        {t('admin.audit.exportCap', { count: AUDIT_EXPORT_MAX_PAGES * AUDIT_PAGE_SIZE })}
      </p>

      {problem ? <ProblemPanel problem={problem} /> : null}

      {page ? (
        <>
          <AuditTable
            rows={auditRows(page.items, context)}
            labels={{
              caption: t('admin.audit.caption'),
              when: t('admin.audit.column.when'),
              actor: t('admin.audit.column.actor'),
              role: t('admin.audit.column.role'),
              action: t('admin.audit.column.action'),
              target: t('admin.audit.column.target'),
              change: t('admin.audit.column.change'),
              empty: t('admin.audit.empty'),
            }}
          />

          <div className="flex flex-wrap items-center gap-sm">
            {selection.cursor ? (
              // Back to the first page, because a cursor page has no predecessor:
              // the envelope carries only the next one, so a "previous" link would
              // be a guess. Saying "start again" is the true affordance.
              <Link
                href={auditHref('/audit-log', selection, { cursor: null })}
                className="text-body-sm underline underline-offset-2"
              >
                {t('admin.audit.first')}
              </Link>
            ) : null}

            {page.hasMore && page.cursor ? (
              <Link
                href={auditHref('/audit-log', selection, { cursor: page.cursor })}
                className="text-body-sm underline underline-offset-2"
              >
                {t('admin.audit.next')}
              </Link>
            ) : null}
          </div>
        </>
      ) : null}

      <p className="text-caption text-on-surface-variant">{t('admin.audit.appendOnly')}</p>
    </div>
  );
}

/**
 * An RFC 3339 instant as `<input type="datetime-local">` wants it.
 *
 * The control has no timezone, so the value is trimmed to minutes and left in the
 * form it arrived in — the alternative is converting through the *container's*
 * zone, which would move an operator's chosen hour on a host that is not in
 * Colombo. The hint beside the field says which clock the filter is read in.
 */
function localValue(instant: string | undefined): string {
  if (!instant) return '';
  return instant.slice(0, 16);
}
