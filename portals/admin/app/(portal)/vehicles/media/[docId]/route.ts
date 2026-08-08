import { relayDocument } from '@/server/document-media';

/**
 * SCR-AP-015's document thumbnails, and the full-size renditions SCR-AP-003b draws
 * from a vehicle's record.
 *
 * The same relay as `/verification/media/[docId]` and deliberately a **different
 * URL**. `proxy.ts` gates a route on the screen its path resolves to, and the two
 * screens are two nav items behind two URD §2.3 rows: the vehicle directory is
 * "Fleet live map & per-vehicle analytics", the queues are "Driver/vehicle
 * verification". A Support CSR holds the first and not the second, so routing this
 * through the verification path would answer 403 for every thumbnail on a screen
 * they are permitted to open — and adding an exemption to `routes.ts` would hand
 * the queues' media to anyone holding any screen at all.
 *
 * `media` is a static segment, so it out-ranks the sibling `[vehicleId]` and a
 * document id is never read as a vehicle.
 */

export const dynamic = 'force-dynamic';

export async function GET(
  request: Request,
  context: { params: Promise<{ docId: string }> },
): Promise<Response> {
  const { docId } = await context.params;
  return relayDocument(request, docId, `/vehicles/media/${docId}`);
}
