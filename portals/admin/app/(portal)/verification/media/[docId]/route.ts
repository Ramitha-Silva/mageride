import { relayDocument } from '@/server/document-media';

/**
 * SCR-AP-003b's bytes: the verification screens' door onto the audited document
 * viewer.
 *
 * The relay itself — why the browser cannot make this call, why the redirect is
 * passed on rather than followed, and why one view is one `DOC_VIEW` row — lives in
 * `src/server/document-media.ts`. What this file contributes is the **path**, and
 * the path is the point: `proxy.ts` resolves `/verification/**` to the verification
 * nav item, so an officer who may open SCR-AP-003a may fetch its thumbnails and a
 * caller who may not is refused before admin-bff is asked. C109's vehicle detail
 * has its own handler under `/vehicles` for the same reason.
 */

export const dynamic = 'force-dynamic';

export async function GET(
  request: Request,
  context: { params: Promise<{ docId: string }> },
): Promise<Response> {
  const { docId } = await context.params;
  return relayDocument(request, docId, `/verification/media/${docId}`);
}
