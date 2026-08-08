import { read } from '@/api/client';
import { localProblem, ProblemError, type ProblemDetails } from '@/api/problem';
import { documentPath, isSubjectId } from '@/api/verification';

/**
 * The portal's relay of `GET /v1/admin/documents/{docId}` — the audited document
 * viewer (AL-39), shared by every screen that renders a stored document.
 *
 * ## Why the browser cannot fetch the document itself
 *
 * The shell's fourth load-bearing decision is that the browser never holds a token
 * and never talks to the gateway, so an `<img src>` pointed at admin-bff would be
 * an anonymous request answered `401`. This makes the call the way every other read
 * is made — the caller's own session, through the data layer — and hands back what
 * admin-bff answered.
 *
 * ## What admin-bff answers, and why this relays rather than fetches
 *
 * A `302` to a **short-lived signed object-storage URL**, minted on the way out of
 * the `DOC_VIEW` row that records the view (C063). So this passes the redirect on:
 * the browser follows it to storage, the bytes never enter this process, and the
 * audit row is already written. Fetching the object here and streaming it would put
 * somebody's licence through the portal's memory for no gain, and would make the
 * signed URL's short life pointless.
 *
 * `apiFetch` is configured `redirect: 'manual'` for exactly this route, which is
 * why the `Location` is available to hand on rather than already followed.
 *
 * ## One view is one row
 *
 * Both `variant=thumb` and `variant=full` come here, so a grid of six thumbnails
 * records six views and opening one in the lightbox records another. That is the
 * contract's own reading — each is a look at somebody's document — and it is why
 * the response is `no-store`: a cached rendition served to a later caller would be
 * a read with no row behind it.
 *
 * ## Why it is a function and not one route
 *
 * **Δ C109.** The relay is one implementation, but it cannot be one URL. A route
 * handler is gated by `proxy.ts` on the screen its path resolves to, so a vehicle's
 * documents fetched through `/verification/media/…` would be gated on the
 * *verification* nav item — and a Support CSR holding the vehicle directory and not
 * the queues would get a 403 for every thumbnail on SCR-AP-015. Each screen group
 * therefore owns a handler under its own path, and both are three lines over this.
 */
export async function relayDocument(request: Request, docId: string, instance: string): Promise<Response> {
  // The id goes into a path this process builds. admin-bff routes the document
  // viewer on `{docId:guid}` and would refuse anything else anyway; checking the
  // shape here means the refusal never depends on that.
  if (!isSubjectId(docId)) {
    return problemResponse(localProblem('not-found', 404, instance, 'Not a document id.'));
  }

  const variant = new URL(request.url).searchParams.get('variant') === 'thumb' ? 'thumb' : 'full';

  let answer: { location?: string | null };
  try {
    answer = await read<{ location?: string | null }>({
      path: documentPath(docId),
      searchParams: { variant },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return problemResponse(error.problem);
  }

  const location = answer?.location;

  // A `200` here means admin-bff answered a body rather than a redirect, which is
  // not a shape this route knows how to be. Better a visible failure than an
  // `<img>` whose source is whatever that body happened to serialise to.
  if (!isStorageUrl(location)) {
    return problemResponse(
      localProblem(
        'dependency-unavailable',
        502,
        instance,
        'The document viewer did not answer a redirect to object storage.',
      ),
    );
  }

  return new Response(null, {
    status: 302,
    headers: {
      location,
      'cache-control': 'no-store',
      // The path this redirect came from carries a document id. Storage has no
      // use for it and no claim on it.
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
