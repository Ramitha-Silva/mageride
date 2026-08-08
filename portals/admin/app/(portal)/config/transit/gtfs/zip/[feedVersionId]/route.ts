import { read } from '@/api/client';
import { localProblem, ProblemError, type ProblemDetails } from '@/api/problem';
import { gtfsDownloadPath, isFeedVersionId } from '@/api/transit';

/**
 * SCR-AP-016's **Download zip** — the original feed, relayed as a redirect
 * (US-28.3, BR-32.3).
 *
 * ## The redirect is passed on, not followed
 *
 * `GET …/versions/{id}/download` answers `302` with a short-lived signed URL, for
 * the reason object storage always does: a browser following a redirect does not
 * carry the bearer that authorised it, so the signature *is* the credential —
 * scoped to one feed version and one expiry, which is what makes a link pasted
 * into a ticket stop working. This handler hands that `Location` on, so the 200 MB
 * of zip never enters this process. `apiFetch` is `redirect: 'manual'` for exactly
 * this case, and C106's document viewer takes the same shape for the same reason.
 *
 * ## Why the zips exist at all
 *
 * Rollback is a **re-import**, not a restore: activating an archived version loads
 * `transit_staging.gtfs_*` from that version's stored zip and swaps it in. So the
 * ≥ 12-month retention BR-32.3 asks for is what makes US-28.3's one-click rollback
 * possible, and nothing on this platform deletes one — a collected zip is a version
 * that can no longer be rolled back to.
 *
 * ## Why it is under `/config/transit/gtfs`
 *
 * `resolveRoute` resolves it to the `gtfs` screen, so `proxy.ts` gates the download
 * on the same nav item as the page — no entry in `src/server/routes.ts` and no
 * exemption. transit-svc gates it again, which is the authorization (US-21.1).
 */

export const dynamic = 'force-dynamic';

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ feedVersionId: string }> },
): Promise<Response> {
  const { feedVersionId } = await params;
  const instance = `/config/transit/gtfs/zip/${feedVersionId}`;

  if (!isFeedVersionId(feedVersionId)) {
    return problemResponse(localProblem('not-found', 404, instance, 'Not a feed version id.'));
  }

  let answer: { location?: string | null };
  try {
    answer = await read<{ location?: string | null }>({ path: gtfsDownloadPath(feedVersionId) });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return problemResponse(error.problem);
  }

  const location = answer?.location;

  // A body where a redirect was expected is not a shape this route knows how to
  // be. Better a visible failure than sending an operator to whatever that body
  // happened to serialise to.
  if (!isStorageUrl(location)) {
    return problemResponse(
      localProblem(
        'dependency-unavailable',
        502,
        instance,
        'The feed download did not answer a redirect to object storage.',
      ),
    );
  }

  return new Response(null, {
    status: 302,
    headers: {
      location,
      'cache-control': 'no-store',
      // The path this redirect came from names a feed version. Storage has no use
      // for it and no claim on it.
      'referrer-policy': 'no-referrer',
    },
  });
}

/** An absolute `http(s)` URL, and nothing else — a relay is not an open redirect. */
function isStorageUrl(value: string | null | undefined): value is string {
  if (!value) return false;

  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function problemResponse(problem: ProblemDetails): Response {
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
