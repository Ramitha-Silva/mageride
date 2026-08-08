import { download } from '@/api/client';
import {
  TRANSACTIONS_CSV_PATH,
  transactionsSearch,
  transactionsSelection,
} from '@/api/finance';
import { ProblemError, type ProblemDetails } from '@/api/problem';

/**
 * SCR-AP-006's CSV export: `GET /v1/admin/finance/transactions.csv`, relayed.
 *
 * The same three properties C105's dashboard export was built for, restated
 * because they are the reason this is a route handler and not a link:
 *
 *  - **the browser never holds a token**, so the request leaves the Next server
 *    with the bearer attached here;
 *  - **the bytes are relayed, not rendered.** admin-bff builds the file from the
 *    same `IFinanceService` call that answers `/finance/transactions`, and its own
 *    description is "byte-for-byte the rows `listWalletTransactions` returns for
 *    the same query — one query, three renderings". A portal that formatted its own
 *    CSV from the JSON would be a second implementation of one document;
 *  - **it is under `/finance/transactions`**, so `resolveRoute` gates it on the
 *    `transactions` nav item — the same gate as the page it belongs to, with no
 *    entry in `src/server/routes.ts` and no exemption. admin-bff gates it again on
 *    Finance · Read, which is the actual authorization.
 *
 * The file carries admin-bff's own `#` preamble naming the window, the timezone,
 * the kinds, the row count and the money unit, because a figure with no stated
 * window is unfalsifiable once the request that produced it is gone.
 */

export const dynamic = 'force-dynamic';

const FALLBACK_FILENAME = 'mageride-wallet-transactions.csv';

export async function GET(request: Request): Promise<Response> {
  const selection = transactionsSelection(
    Object.fromEntries(new URL(request.url).searchParams.entries()),
  );

  let file;
  try {
    file = await download({
      path: TRANSACTIONS_CSV_PATH,
      accept: 'text/csv',
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
