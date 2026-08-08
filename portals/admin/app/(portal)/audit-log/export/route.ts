import {
  AUDIT_EXPORT_MAX_PAGES,
  AUDIT_LOG_PATH,
  auditSearch,
  auditSelection,
  type AuditEvent,
  type CursorPage,
} from '@/api/audit-log';
import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import { auditCsv } from '@/components/audit/model';

/**
 * SCR-AP-009's CSV export (US-19.3).
 *
 * ## Why this one renders instead of relaying
 *
 * Every other export on this surface relays bytes a service produced, and C105
 * states the rule: "a portal that formatted its own CSV out of the JSON would be a
 * second implementation of the same document". There is no first implementation.
 * `GET /v1/admin/audit-log` has no `.csv` sibling — unlike `/dashboard/stats` and
 * `/finance/transactions`, both of which do — while US-19.3 and the wireframe's own
 * "Export CSV" button both ask for one. The choice is between building it here and
 * shipping a screen that cannot do what the story says. It is built here, narrowly,
 * and the C108 handoff asks admin-bff for `GET /v1/admin/audit-log.csv` so this
 * file can be deleted rather than maintained.
 *
 * ## The filters are the screen's, exactly
 *
 * `auditSelection` and `auditSearch` are the same two functions the page calls, so
 * "the CSV contains the rows the filter is showing" is one query asked twice rather
 * than two queries that agree today. The cursor is dropped: an export is of the
 * filter, not of the page somebody happened to be on.
 *
 * ## The cap is stated three times, because a silent truncation is the one failure
 * an audit export cannot have
 *
 * A cursor-paged source has no natural end, so the handler follows at most
 * {@link AUDIT_EXPORT_MAX_PAGES} pages. That is said on the screen beside the link,
 * written into the file's `#` preamble alongside the filters, and flagged there
 * explicitly when it actually bit. A file that quietly stopped at two thousand rows
 * would read as "these are all the events", which is the one thing it must never
 * be mistaken for.
 */

export const dynamic = 'force-dynamic';

const FILENAME = 'mageride-audit-log.csv';

export async function GET(request: Request): Promise<Response> {
  const selection = auditSelection(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  );

  const events: AuditEvent[] = [];
  let cursor: string | null = null;
  let truncated = false;

  try {
    for (let pageNumber = 0; pageNumber < AUDIT_EXPORT_MAX_PAGES; pageNumber += 1) {
      const page: CursorPage<AuditEvent> = await read<CursorPage<AuditEvent>>({
        path: AUDIT_LOG_PATH,
        searchParams: auditSearch(selection, { cursor }),
      });

      events.push(...page.items);

      if (!page.hasMore || !page.cursor) {
        cursor = null;
        break;
      }

      cursor = page.cursor;
      truncated = pageNumber === AUDIT_EXPORT_MAX_PAGES - 1;
    }
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return problemResponse(error.problem);
  }

  const body = `${preamble(selection, events.length, truncated)}\r\n${auditCsv(events)}\r\n`;

  // UTF-8 BOM, written as an escape so it is visible in the source.
  return new Response(`\uFEFF${body}`, {
    status: 200,
    headers: {
      // UTF-8 with a BOM, matching the two exports admin-bff renders: a
      // spreadsheet that opens the file in the host's ANSI code page mangles
      // every id it contains.
      'content-type': 'text/csv; charset=utf-8',
      'content-disposition': `attachment; filename="${FILENAME}"`,
      'cache-control': 'no-store',
    },
  });
}

/**
 * The `#` header naming the query, the row count and the truncation — the same
 * shape admin-bff puts on its own exports, "because a figure with no stated window
 * is unfalsifiable once the request that produced it is gone".
 */
function preamble(
  selection: ReturnType<typeof auditSelection>,
  rows: number,
  truncated: boolean,
): string {
  const lines = [
    '# MageRide admin audit log (audit.events)',
    `# actorId,${selection.actorId ?? '(any)'}`,
    `# action,${selection.action ?? '(any)'}`,
    `# subjectId,${selection.subjectId ?? '(any)'}`,
    `# from,${selection.from ?? '(unbounded)'}`,
    `# to,${selection.to ?? '(unbounded)'}`,
    `# rows,${rows}`,
  ];

  if (truncated) {
    lines.push(
      `# TRUNCATED,this export stopped at ${AUDIT_EXPORT_MAX_PAGES} pages and is NOT the whole result`,
    );
  }

  return lines.join('\r\n');
}

/** A failure as `application/problem+json` — never a 200 carrying a partial file. */
function problemResponse(problem: ProblemDetails): Response {
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
