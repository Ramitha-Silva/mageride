import Link from 'next/link';
import { notFound, redirect } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { isSubjectId, subjectPath, type VerificationDetail } from '@/api/verification';
import { ProblemPanel } from '@/components/ProblemPanel';
import { ScreenPlaceholder } from '@/components/ScreenPlaceholder';
import { DecisionRail } from '@/components/verification/DecisionRail';
import { DocumentGrid } from '@/components/verification/DocumentGrid';
import { FieldsTable } from '@/components/verification/FieldsTable';
import {
  documentTiles,
  fieldRows,
  pendingSummary,
  stepRows,
  type RenderContext,
} from '@/components/verification/model';
import {
  approveLabelKey,
  backHref,
  mediaHref,
  queryString,
} from '@/components/verification/links';
import { getLocale, getTranslator } from '@/i18n/server';
import { resolveRoute } from '@/server/routes';

/**
 * **SCR-AP-003a · `verification_detail`** — one queue entry: its documents, its
 * AI-extracted fields, and the decision rail (AL-39, US-2.9/2.10a/2.4a).
 *
 * ## The one rule this screen exists to hold
 *
 * **Approve is disabled while any flagged field is unconfirmed.** `approvable`
 * comes from the same read that draws the per-step breakdown beside the button,
 * so the gate and the explanation for it cannot disagree; `decideSubject` refuses
 * to send it anyway; and admin-bff answers `409` if it is sent, which is the only
 * one of the three that is authorization (AL-06, US-21.1).
 *
 * ## Why this file can be asked for a screen that is not this one
 *
 * `/verification/expiring` is the **document-expiry** screen — a different nav
 * item, gated on its own entry in `src/server/routes.ts` — and a single dynamic
 * segment out-ranks the shell's catch-all. So a path that resolves to another
 * screen is handed straight back to `<ScreenPlaceholder>` rather than read as a
 * subject id. When C110 lands `verification/expiring/page.tsx` the static segment
 * wins over this one and the branch stops being reachable.
 *
 * ## An organisation is not this screen
 *
 * `GET /v1/admin/verification/{id}` answers for all three subjects, but D2 gives
 * an organisation its own screen (SCR-AP-003c) because what an officer reads
 * there is KYC and bank details rather than extracted fields. A pasted URL is
 * redirected instead of rendering an empty fields table.
 */

export const dynamic = 'force-dynamic';

export default async function VerificationDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ subjectId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { subjectId } = await params;
  const query = await searchParams;
  const pathname = `/verification/${subjectId}`;

  if (resolveRoute(pathname)?.key !== 'verification') {
    return <ScreenPlaceholder pathname={pathname} />;
  }

  if (!isSubjectId(subjectId)) notFound();

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };
  const search = queryString(query);

  let detail: VerificationDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<VerificationDetail>({ path: subjectPath(subjectId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  if (detail?.subject.type === 'org') {
    redirect(`/verification/org/${subjectId}${search ? `?${search}` : ''}`);
  }

  const queue = backHref(search);

  if (!detail) {
    return (
      <div className="flex flex-col gap-md">
        <BackLink href={queue} label={t('admin.verification.detail.back')} />
        {problem ? <ProblemPanel problem={problem} /> : null}
      </div>
    );
  }

  const approvable = detail.approvable ?? false;
  const summary = pendingSummary(detail.fields, approvable, t);
  const tiles = documentTiles(
    detail.documents,
    {
      viewer: (docId) => `${pathname}/doc/${docId}${search ? `?${search}` : ''}`,
      media: (docId) => mediaHref(docId, 'thumb'),
    },
    context,
  );

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-xs">
        <BackLink href={queue} label={t('admin.verification.detail.back')} />
        <h2 className="min-w-0 text-title font-display break-all">
          {detail.subject.displayName ?? detail.subject.id}
        </h2>
        <span className="flex-1" />
        <StatusPill tone={summary.tone}>{summary.label}</StatusPill>
      </div>

      <p className="text-caption break-all text-on-surface-variant">{detail.subject.id}</p>

      <DocumentGrid
        tiles={tiles}
        labels={{
          heading: t('admin.verification.doc.heading'),
          hint: t('admin.verification.doc.hint'),
          empty: t('admin.verification.doc.empty'),
          note: t('admin.verification.doc.note'),
        }}
      />

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <FieldsTable
          rows={fieldRows(detail.fields, context)}
          subjectId={detail.subject.id}
          subjectType={detail.subject.type}
          returnTo={pathname}
          labels={{
            heading: t('admin.verification.fields.heading'),
            engine: t('admin.verification.fields.engine'),
            caption: t('admin.verification.fields.heading'),
            field: t('admin.verification.column.field'),
            value: t('admin.verification.column.value'),
            source: t('admin.verification.column.source'),
            status: t('admin.verification.column.status'),
            action: t('admin.verification.column.action'),
            empty: t('admin.verification.fields.empty'),
            note: t('admin.verification.fields.note'),
            decision: {
              confirm: t('common.confirm'),
              edit: t('common.edit'),
              editConfirm: t('admin.verification.field.editConfirm'),
              cancel: t('common.cancel'),
              correctedValue: t('admin.verification.field.correctedValue'),
              working: t('admin.verification.field.working'),
            },
          }}
        />

        <DecisionRail
          subjectId={detail.subject.id}
          subjectType={detail.subject.type}
          approvable={approvable}
          steps={stepRows(detail.steps, t)}
          returnTo={queue}
          labels={{
            heading: t('admin.verification.decision.heading'),
            stepsHeading: t('admin.verification.decision.steps'),
            reason: t('admin.verification.decision.reason'),
            reasonHint: t('admin.verification.decision.reasonHint'),
            approve: t(approveLabelKey(detail.subject.type)),
            reject: t('admin.verification.decision.reject'),
            working: t('admin.verification.decision.working'),
            blocked: t('admin.verification.approve.blocked'),
            audit: t('admin.audit.notice'),
          }}
        />
      </div>
    </div>
  );
}

function BackLink({ href, label }: { href: string; label: string }) {
  return (
    <Link
      href={href}
      className="inline-flex h-10 items-center rounded-sm border border-outline px-sm text-body-sm text-on-surface-variant hover:bg-surface-variant"
    >
      <span aria-hidden="true">{'‹ '}</span>
      {label}
    </Link>
  );
}
