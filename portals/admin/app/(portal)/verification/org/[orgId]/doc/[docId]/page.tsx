import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { isSubjectId, orgPath, type OrgVerification } from '@/api/verification';
import { ProblemPanel } from '@/components/ProblemPanel';
import { DocumentViewer } from '@/components/verification/DocumentViewer';
import { mediaHref, queryString } from '@/components/verification/links';
import { documentTiles, viewerPosition, type RenderContext } from '@/components/verification/model';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-003b**, opened from an organisation's evidence grid (SCR-AP-003c).
 *
 * The same viewer, over a different read: an organisation's documents come from
 * `…/verification/org/{orgId}` rather than from the subject-agnostic detail, so
 * this page resolves the set and its paging the way the screen the officer came
 * from did. Closing returns to that screen, not to a driver's.
 *
 * The AL-49 payout documents carry no `capturedVia` — fleet-svc leaves it null
 * deliberately, because AL-43's provenance is about onboarding photographs and a
 * value invented here would put a fraud signal on every bank statement on the
 * platform. So this viewer usually shows none, which is correct.
 */

export const dynamic = 'force-dynamic';

export default async function OrgDocumentViewerPage({
  params,
  searchParams,
}: {
  params: Promise<{ orgId: string; docId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { orgId, docId } = await params;
  const query = await searchParams;

  if (!isSubjectId(orgId) || !isSubjectId(docId)) notFound();

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };
  const search = queryString(query);
  const detailHref = `/verification/org/${orgId}${search ? `?${search}` : ''}`;

  let org: OrgVerification | null = null;
  let problem: ProblemDetails | null = null;

  try {
    org = await read<OrgVerification>({ path: orgPath(orgId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  if (problem) return <ProblemPanel problem={problem} />;
  if (!org) notFound();

  const tiles = documentTiles(
    org.documents,
    {
      viewer: (id) => `/verification/org/${orgId}/doc/${id}${search ? `?${search}` : ''}`,
      media: (id) => mediaHref(id, 'full'),
    },
    context,
  );

  const position = viewerPosition(tiles, docId);
  if (!position) notFound();

  return (
    <DocumentViewer
      src={position.current.src}
      title={t('admin.verification.viewer.title', {
        document: position.current.label,
        position: position.current.position,
      })}
      closeHref={detailHref}
      {...(position.previous ? { previousHref: position.previous.href } : {})}
      {...(position.next ? { nextHref: position.next.href } : {})}
      {...(position.current.capturedVia ? { provenance: position.current.capturedVia } : {})}
      labels={{
        close: t('common.close'),
        previous: t('admin.verification.viewer.previous'),
        next: t('common.next'),
        zoomIn: t('admin.verification.viewer.zoomIn'),
        zoomOut: t('admin.verification.viewer.zoomOut'),
        rotate: t('admin.verification.viewer.rotate'),
        reset: t('admin.verification.viewer.reset'),
        image: position.current.label,
      }}
    />
  );
}
