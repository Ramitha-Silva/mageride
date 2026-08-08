import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { isSubjectId, subjectPath, type VerificationDetail } from '@/api/verification';
import { ProblemPanel } from '@/components/ProblemPanel';
import { DocumentViewer } from '@/components/verification/DocumentViewer';
import { mediaHref, queryString } from '@/components/verification/links';
import { documentTiles, viewerPosition, type RenderContext } from '@/components/verification/model';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-003b · `document_viewer`** — a driver's or a vehicle's document, full
 * size (AL-39, US-24.8).
 *
 * The subject is read again rather than passed through the URL, and that is what
 * makes prev/next mean "the next of *this entry's* documents": paging is over the
 * set admin-bff answers for the subject, not over whatever the previous page
 * happened to have rendered. It also means a pasted link opens with its paging
 * intact, and a document that does not belong to this subject is a `404` instead
 * of a viewer showing somebody else's licence.
 *
 * The image itself is never fetched here — see `verification/media/[docId]`.
 */

export const dynamic = 'force-dynamic';

export default async function DocumentViewerPage({
  params,
  searchParams,
}: {
  params: Promise<{ subjectId: string; docId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { subjectId, docId } = await params;
  const query = await searchParams;

  if (!isSubjectId(subjectId) || !isSubjectId(docId)) notFound();

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };
  const search = queryString(query);
  const detailHref = `/verification/${subjectId}${search ? `?${search}` : ''}`;

  let detail: VerificationDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<VerificationDetail>({ path: subjectPath(subjectId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  if (problem) return <ProblemPanel problem={problem} />;
  if (!detail) notFound();

  const tiles = documentTiles(
    detail.documents,
    {
      viewer: (id) => `/verification/${subjectId}/doc/${id}${search ? `?${search}` : ''}`,
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
