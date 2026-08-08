import { download } from '@/api/client';
import {
  TRANSACTIONS_PDF_PATH,
  transactionsSearch,
  transactionsSelection,
} from '@/api/finance';
import { ProblemError, type ProblemDetails } from '@/api/problem';

/**
 * SCR-AP-006's PDF export: `GET /v1/admin/finance/transactions.pdf`, relayed.
 *
 * The CSV handler beside this one states the three reasons a relay is a route
 * handler; they apply unchanged. What is different here is the **document**:
 * admin-bff lays the same rows out as an A4 table in one of the base-14 fonts every
 * conforming reader is required to have, so nothing is embedded and no rendering
 * library is on the platform's dependency list. The `entryId` column is dropped
 * there — a page of UUIDs is a page nobody reads — and the CSV keeps it, which is
 * why both links are drawn rather than one.
 *
 * **It is not for anything trilingual.** WinAnsi cannot draw Sinhala or Tamil, and
 * the contract is explicit that this renderer "must not be extended to fake a
 * script it has no glyphs for" (D-26). The screen says so beside the link, in the
 * operator's own language, because the one person who needs to know is the one
 * about to hand the file to somebody who reads Sinhala.
 */

export const dynamic = 'force-dynamic';

const FALLBACK_FILENAME = 'mageride-wallet-transactions.pdf';

export async function GET(request: Request): Promise<Response> {
  const selection = transactionsSelection(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  );

  let file;
  try {
    file = await download({
      path: TRANSACTIONS_PDF_PATH,
      accept: 'application/pdf',
      searchParams: transactionsSearch(selection),
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
      'cache-control': 'no-store',
    },
  });
}

/** A failure as `application/problem+json` — never a 200 carrying an HTML page. */
function problemResponse(problem: ProblemDetails): Response {
  const status = problem.status >= 400 && problem.status <= 599 ? problem.status : 502;

  return new Response(JSON.stringify(problem), {
    status,
    headers: { 'content-type': 'application/problem+json', 'cache-control': 'no-store' },
  });
}
