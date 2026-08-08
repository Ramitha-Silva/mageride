import { upload } from '@/api/client';
import { localProblem, ProblemError, type ProblemDetails } from '@/api/problem';
import {
  GTFS_UPLOADS_PATH,
  MAX_FEED_BYTES,
  MULTIPART_OVERHEAD_BYTES,
  type FeedUploadAccepted,
} from '@/api/transit';

/**
 * SCR-AP-016's upload relay — `POST /v1/admin/transit/gtfs/uploads`, streamed
 * (US-28.1, BR-32.1).
 *
 * ## Why the browser posts here and not at the platform
 *
 * The shell's fourth load-bearing decision: the browser holds no token and never
 * talks to the gateway. A form posting straight at transit-svc would be an
 * anonymous 401 — so the request leaves the Next server like every other, with the
 * operator's bearer attached by the data layer.
 *
 * ## Why the body is a stream and not a form
 *
 * A feed is up to 200 MB. `request.formData()` would materialise the whole file in
 * this process before a byte left for the platform, on a route whose only job is
 * to carry it — the same argument `GtfsProxyEndpoints` makes on the other side of
 * the same hop, and the reason transit-svc reads the multipart with a
 * `MultipartReader` rather than `ReadFormAsync`. So `request.body` is handed to
 * {@link upload} untouched, `Content-Type` and boundary included, and nothing here
 * parses the multipart at all.
 *
 * ## The size check that is worth making here
 *
 * A declared `Content-Length` over the ceiling is refused **before a connection
 * upstream is opened** — there is no reason to relay 400 MB to be told no. That is
 * the second of the three guards BR-32.1 gets: the dropzone is the first (and
 * saves the operator the upload entirely), and transit-svc's own Kestrel limit and
 * object-store byte count are the third, which is the only one that is a gate,
 * because a client that declares nothing is a client that declared nothing.
 *
 * ## Why it is under `/config/transit/gtfs`
 *
 * `resolveRoute` resolves `/config/transit/gtfs/upload` to the `gtfs` screen, so
 * `proxy.ts` gates the upload on the same nav item as the page it belongs to — no
 * entry in `src/server/routes.ts` and no exemption anywhere. transit-svc gates it
 * again on Admin/Super Admin, which is the actual authorization (US-21.1).
 */

export const dynamic = 'force-dynamic';

/** Room for the multipart envelope, matching `GtfsAdminEndpoints.RequireWithinLimit`. */
const CEILING = MAX_FEED_BYTES + MULTIPART_OVERHEAD_BYTES;

export async function POST(request: Request): Promise<Response> {
  const instance = '/config/transit/gtfs/upload';

  const contentType = request.headers.get('content-type');
  if (!contentType?.toLowerCase().startsWith('multipart/form-data')) {
    return problemResponse(
      localProblem(
        'validation-failed',
        400,
        instance,
        'Expected multipart/form-data with a `file` part carrying the GTFS zip.',
      ),
    );
  }

  const declared = Number(request.headers.get('content-length') ?? '');
  if (Number.isFinite(declared) && declared > CEILING) {
    return problemResponse(
      localProblem(
        'payload-too-large',
        413,
        instance,
        `The upload declares ${declared} bytes; the limit is ${MAX_FEED_BYTES} (BR-32.1: 200 MB).`,
      ),
    );
  }

  if (!request.body) {
    return problemResponse(
      localProblem('validation-failed', 400, instance, 'The request carries no body.'),
    );
  }

  try {
    const { data } = await upload<FeedUploadAccepted>({
      path: GTFS_UPLOADS_PATH,
      body: request.body,
      contentType,
      // The row an auditor will find is transit-svc's, written inside the same
      // transaction as the `gtfs_feed_versions` insert. See `TransitAuditAction`.
      audit: { action: 'GTFS_FEED_UPLOADED', entity: 'gtfs_feed' },
    });

    // 202 and not 201: validation has not run, so what exists is an upload
    // awaiting a verdict — transit-svc's own wording, relayed rather than
    // reinterpreted.
    return Response.json(data, { status: 202, headers: { 'cache-control': 'no-store' } });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // Relayed whole, extensions and all: `409 feed-duplicate` carries the version
    // that already holds these bytes, and the dropzone's inline error is built
    // from it.
    return problemResponse(error.problem);
  }
}

function problemResponse(problem: ProblemDetails): Response {
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
