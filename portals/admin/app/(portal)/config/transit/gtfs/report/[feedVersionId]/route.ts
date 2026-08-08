import { download } from '@/api/client';
import { localProblem, ProblemError, type ProblemDetails } from '@/api/problem';
import {
  gtfsReportPath,
  isFeedVersionId,
  isReportFormat,
  REPORT_MEDIA_TYPES,
  type ReportFormat,
} from '@/api/transit';

/**
 * SCR-AP-016's **Download error report** — `GET …/uploads/{id}/report`, relayed
 * (US-28.1, BR-32.1).
 *
 * ## The bytes are relayed, never re-rendered
 *
 * transit-svc writes both forms from the same `FeedValidationReport`, so the CSV
 * and the JSON agree because they are one document — the rule C105's export and
 * C108's transactions report are both under. A portal that formatted its own CSV
 * from the JSON would be a second implementation of the same file, and the two
 * would diverge the first time either changed. What this handler adds is the
 * session and the `Content-Disposition`.
 *
 * ## Two formats, and CSV is the one the screen offers first
 *
 * "What an operator actually fixes the feed from", in D3's own words: one row per
 * finding, severity first, so a spreadsheet sorts and filters it without a
 * formula. JSON is beside it because a report is also the thing somebody diffs
 * between two uploads of the same feed.
 *
 * ## It is offered on every version, not only a failed one
 *
 * BR-32.1's report carries **errors and warnings**, and a validated feed with
 * twelve renamed stops has nothing in its error summary and twelve things worth
 * reading. Restricting the download to `failed` would hide the half of the report
 * that is about a feed somebody is about to make live.
 */

export const dynamic = 'force-dynamic';

const DEFAULT_FORMAT: ReportFormat = 'csv';

export async function GET(
  request: Request,
  { params }: { params: Promise<{ feedVersionId: string }> },
): Promise<Response> {
  const { feedVersionId } = await params;
  const instance = `/config/transit/gtfs/report/${feedVersionId}`;

  // The id goes into a path this process builds. transit-svc routes on
  // `{feedVersionId:guid}` and would refuse anything else anyway; checking the
  // shape here means the refusal never depends on that.
  if (!isFeedVersionId(feedVersionId)) {
    return problemResponse(localProblem('not-found', 404, instance, 'Not a feed version id.'));
  }

  const requested = new URL(request.url).searchParams.get('format');
  const format = isReportFormat(requested) ? requested : DEFAULT_FORMAT;

  let file;
  try {
    file = await download({
      path: gtfsReportPath(feedVersionId),
      accept: REPORT_MEDIA_TYPES[format],
      searchParams: { format },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return problemResponse(error.problem);
  }

  return new Response(file.body, {
    status: 200,
    headers: {
      'content-type': file.contentType,
      'content-disposition': `attachment; filename="${
        file.filename ?? `gtfs-${feedVersionId}-report.${format}`
      }"`,
      // A validation report is a window onto the platform under a per-caller RBAC
      // evaluation, exactly as every read is. No intermediary keeps a copy.
      'cache-control': 'no-store',
    },
  });
}

/**
 * A failure as `application/problem+json` and the status it actually was.
 *
 * Deliberately not a redirect back to the screen: this response goes to whatever
 * asked for the file, and a 200 carrying an HTML page would be a download that
 * silently produced a broken report.
 */
function problemResponse(problem: ProblemDetails): Response {
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
