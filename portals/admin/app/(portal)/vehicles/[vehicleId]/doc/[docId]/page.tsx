import { notFound } from 'next/navigation';

import { read } from '@/api/client';
import { isDirectoryId, vehiclePath, vehicleSelection, type AdminVehicleDetail } from '@/api/directories';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { ProblemPanel } from '@/components/ProblemPanel';
import { vehicleDocHref, vehicleHref, vehicleMediaHref } from '@/components/directories/links';
import { DocumentViewer } from '@/components/verification/DocumentViewer';
import { documentTiles, viewerPosition, type RenderContext } from '@/components/verification/model';
import { getLocale, getTranslator } from '@/i18n/server';

/**
 * **SCR-AP-003b, opened from a vehicle record** — one of SCR-AP-015's documents,
 * full size.
 *
 * The same screen as the verification queue's viewer and the **same component**:
 * `DocumentViewer` handles the zoom, the quarter turns, Escape and the arrow keys,
 * and none of that is written twice. What differs is where the document comes from
 * and which gate the URL sits behind — `GET /v1/admin/vehicles/{id}` rather than
 * `GET /v1/admin/verification/{id}`, under `/vehicles` rather than `/verification`.
 *
 * The vehicle is read again rather than passed through the URL, which is what makes
 * prev/next mean "the next of *this vehicle's* documents": paging is over the set
 * admin-bff answers for the record, not over whatever the previous page happened to
 * render. A document id that does not belong to this vehicle is a `404` instead of
 * a viewer showing somebody else's insurance certificate.
 *
 * **That second read writes a second `PII_READ` row**, because opening a document
 * from a record is another look at that record — and the image itself writes a
 * `DOC_VIEW` row on top of it, through `/vehicles/media/{docId}`. Two rows for two
 * different disclosures is the honest count; see `api/directories.ts`.
 */

export const dynamic = 'force-dynamic';

export default async function VehicleDocumentPage({
  params,
  searchParams,
}: {
  params: Promise<{ vehicleId: string; docId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { vehicleId, docId } = await params;
  const query = await searchParams;

  if (!isDirectoryId(vehicleId) || !isDirectoryId(docId)) notFound();

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };
  const selection = vehicleSelection(query);

  let detail: AdminVehicleDetail | null = null;
  let problem: ProblemDetails | null = null;

  try {
    detail = await read<AdminVehicleDetail>({ path: vehiclePath(vehicleId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    problem = error.problem;
  }

  if (problem) return <ProblemPanel problem={problem} />;
  if (!detail) notFound();

  const tiles = documentTiles(
    detail.documents ?? [],
    {
      viewer: (id) => vehicleDocHref(selection, vehicleId, id),
      media: (id) => vehicleMediaHref(id, 'full'),
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
      closeHref={vehicleHref(selection, vehicleId)}
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
