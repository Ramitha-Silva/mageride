import Link from 'next/link';
import { notFound } from 'next/navigation';

import { StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { isSubjectId, orgPath, type OrgVerification } from '@/api/verification';
import { ProblemPanel } from '@/components/ProblemPanel';
import { DecisionRail } from '@/components/verification/DecisionRail';
import { DocumentGrid } from '@/components/verification/DocumentGrid';
import { backHref, mediaHref, queryString } from '@/components/verification/links';
import { documentTiles, payoutPill, statusPill, type RenderContext } from '@/components/verification/model';
import { OrgKycCard } from '@/components/verification/OrgKycCard';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-003c · `fleetorg_detail`** — a fleet organisation's KYC, its evidence
 * and the approval that unlocks it (AL-39, AL-49, US-13.A7).
 *
 * ## Why an organisation has its own screen
 *
 * `GET /v1/admin/verification/{subjectId}` answers for an org too, with an empty
 * field list and a single `kyc` step — but there is nothing to confirm row by row,
 * because **nothing extracts fields from a business registration or a bank
 * statement**. What an officer reads here is the organisation's own particulars
 * and the AL-49 payout details, and what they decide is the whole submission. So
 * this screen reads `…/verification/org/{orgId}`, which carries exactly that, and
 * shares only the decision rail with SCR-AP-003a.
 *
 * ## The rail has no gate to draw here
 *
 * There are no flagged fields, so Approve is always enabled — which is the
 * contract's own `approvable: true` for an org subject, not an exception this
 * screen makes. The single `kyc` step still shows, because a decision panel with
 * nothing in it reads as "nothing to check".
 *
 * ## Approving verifies the bank details
 *
 * `POST …/{orgId}/approve` sets `payout_profiles.status = 'verified'` (AL-49),
 * which is what BR-31.1 gates Paid classification and Paid-subscription billing
 * on. The card says so beside the account number, because an officer approving a
 * business registration is also authorising where that organisation's money goes.
 */

export const dynamic = 'force-dynamic';

export default async function FleetOrgDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ orgId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { orgId } = await params;
  const query = await searchParams;

  if (!isSubjectId(orgId)) notFound();

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };
  const search = queryString(query);
  const queue = backHref(search);

  let org: OrgVerification | null = null;
  let problem: ProblemDetails | null = null;

  try {
    org = await read<OrgVerification>({ path: orgPath(orgId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  if (!org) {
    return (
      <div className="flex flex-col gap-md">
        <BackLink href={queue} label={t('admin.verification.detail.back')} />
        {problem ? <ProblemPanel problem={problem} /> : null}
      </div>
    );
  }

  const payout = org.payoutProfileStatus ?? org.kyc.payoutProfile?.status ?? null;
  const tiles = documentTiles(
    org.documents,
    {
      viewer: (docId) => `/verification/org/${orgId}/doc/${docId}${search ? `?${search}` : ''}`,
      media: (docId) => mediaHref(docId, 'thumb'),
    },
    context,
  );

  return (
    <div className="flex flex-col gap-md">
      <div className="flex flex-wrap items-center gap-xs">
        <BackLink href={queue} label={t('admin.verification.detail.back')} />
        <h2 className="min-w-0 text-title font-display break-words">{org.kyc.name}</h2>
        <span className="flex-1" />
        <StatusPill tone={statusPill(org.kyc.status, 0, t).tone}>
          {statusPill(org.kyc.status, 0, t).label}
        </StatusPill>
      </div>

      <p className="text-caption break-all text-on-surface-variant">{org.kyc.orgId}</p>

      <div className="flex flex-col gap-md lg:flex-row lg:items-start">
        <OrgKycCard
          kyc={org.kyc}
          payoutStatus={payout ? payoutPill(payout, t) : null}
          labels={{
            heading: t('admin.verification.org.heading'),
            caption: t('admin.verification.org.caption'),
            registeredName: t('admin.verification.org.registeredName'),
            registrationNo: t('admin.verification.org.registrationNo'),
            contactPhone: t('admin.verification.org.contactPhone'),
            contactEmail: t('admin.verification.org.contactEmail'),
            address: t('admin.verification.org.address'),
            rejectionReason: t('admin.verification.org.rejectionReason'),
            payoutHeading: t('admin.verification.org.payoutHeading'),
            payoutCaption: t('admin.verification.org.payoutCaption'),
            payoutNone: t('admin.verification.org.payoutNone'),
            bank: t('admin.verification.org.bank'),
            branch: t('admin.verification.org.branch'),
            accountNo: t('admin.verification.org.accountNo'),
            accountHolder: t('admin.verification.org.accountHolder'),
            payoutRejection: t('admin.verification.org.payoutRejection'),
            payoutGate: t('admin.verification.org.payoutGate'),
          }}
        />

        <DecisionRail
          subjectId={org.kyc.orgId}
          subjectType="org"
          approvable
          steps={[]}
          returnTo={queue}
          labels={{
            heading: t('admin.verification.decision.heading'),
            stepsHeading: t('admin.verification.decision.steps'),
            reason: t('admin.verification.decision.reason'),
            reasonHint: t('admin.verification.decision.reasonHint'),
            approve: t('admin.verification.decision.approveOrg'),
            reject: t('admin.verification.decision.reject'),
            working: t('admin.verification.decision.working'),
            blocked: t('admin.verification.approve.blocked'),
            audit: t('admin.audit.notice'),
          }}
        />
      </div>

      <DocumentGrid
        tiles={tiles}
        labels={{
          heading: t('admin.verification.org.documents'),
          hint: t('admin.verification.doc.hint'),
          empty: t('admin.verification.org.documentsEmpty'),
          note: t('admin.verification.doc.note'),
        }}
      />
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
