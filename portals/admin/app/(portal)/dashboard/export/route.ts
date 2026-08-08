import { download } from '@/api/client';
import { STATS_CSV_PATH, statsSearch, statsSelection } from '@/api/dashboard';
import { localProblem, ProblemError, type ProblemDetails } from '@/api/problem';

/**
 * SCR-AP-002's CSV export (AL-38): `GET /v1/admin/dashboard/stats.csv`, relayed.
 *
 * ## Why a route handler and not a link to the API
 *
 * The browser never talks to the gateway and never holds a token — the shell's
 * fourth load-bearing decision. An `<a href>` straight at admin-bff would need
 * one, so the request leaves the Next server like every other, with the bearer
 * attached here.
 *
 * ## Why the bytes are relayed rather than rendered
 *
 * admin-bff builds the file from the same `IDashboardStatsService` call that
 * answers `/dashboard/stats`, so the file and the screen agree because they are
 * one computation. A portal that formatted its own CSV out of the JSON would be a
 * second implementation of the same document — and the first change to either
 * would make the DoD's "the CSV contains exactly the filtered figures on screen"
 * a claim nobody was checking. This handler adds the session and the
 * `Content-Disposition`, and nothing else.
 *
 * ## Why it is under `/dashboard`
 *
 * `resolveRoute` resolves `/dashboard/export` to the `dashboard` screen, so
 * `proxy.ts` gates the download on the same nav item as the page it belongs to —
 * no entry in `src/server/routes.ts` and no exemption anywhere. admin-bff gates
 * it again on Analytics · Read, which is the actual authorization (US-21.1).
 */

export const dynamic = 'force-dynamic';

/** Used only when admin-bff names no file, which it normally does. */
const FALLBACK_FILENAME = 'mageride-dashboard-stats.csv';

export async function GET(request: Request): Promise<Response> {
  const selection = statsSelection(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  );

  // A half-chosen custom range has no figures behind it, and answering with the
  // current day's under a filename naming a range nobody asked for is the exact
  // substitution C061 refuses to make server-side.
  if (selection.awaitingRange) {
    return problemResponse(
      localProblem(
        'validation-failed',
        400,
        '/dashboard/export',
        'A custom period needs both ends of its range.',
      ),
    );
  }

  let file;
  try {
    file = await download({
      path: STATS_CSV_PATH,
      accept: 'text/csv',
      searchParams: statsSearch(selection),
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return problemResponse(error.problem);
  }

  return new Response(file.body, {
    status: 200,
    headers: {
      'content-type': file.contentType,
      'content-disposition': `attachment; filename="${file.filename ?? FALLBACK_FILENAME}"`,
      // The figures are a window onto other people's platform under a per-caller
      // RBAC evaluation, exactly as every read is. No intermediary keeps a copy.
      'cache-control': 'no-store',
    },
  });
}

/**
 * A failure, as `application/problem+json` and the status it actually was.
 *
 * Deliberately not a redirect back to the screen with a message: this response
 * goes to whatever asked for the file, and a 200 carrying an HTML page would be a
 * download that silently produced a broken CSV.
 */
function problemResponse(problem: ProblemDetails): Response {
  // A problem whose status is not a failure would be a failure served as a
  // success — the operator's spreadsheet would open the refusal as data. The
  // status is always set by `readProblem` or `localProblem`; the clamp is what
  // makes that a property of this handler rather than of its callers.
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
