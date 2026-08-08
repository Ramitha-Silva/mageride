import { NextResponse, type NextRequest } from 'next/server';

import {
  invoiceExportTarget,
  isInvoiceExportFormat,
  type InvoiceExportFormat,
} from '@/api/billing';
import { download } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-010's Download** — fleet-billing-svc's own invoice document, streamed
 * to the operator.
 *
 * ## Why this is a route handler and not a link to the API
 *
 * The browser holds no bearer and cannot reach the gateway: `src/api/http.ts` is
 * `server-only` and the session lives in httpOnly cookies (C111 decision 6). So a
 * `<a href="https://api.mageride.lk/v1/fleets/…/export">` would download a `401`.
 * This handler makes the same org-scoped call the screen makes, with the same
 * bearer and the same row-level scope, and passes the bytes through.
 *
 * ## The document is the service's, and is not rebuilt here
 *
 * `GET …/billing/{invoiceId}/export?format=csv|pdf` renders both: the CSV "prints
 * money twice — rupees for a bank reconciliation, integer minor units for a
 * reconciliation against this platform", and its `TOTAL` row is Σ of the rows
 * above it "computed from the lines rather than copied from the invoice, so if the
 * two ever disagreed the file an operator holds should show it". A file this
 * portal composed would be a second document about the same money, and the two
 * would drift.
 *
 * That is the opposite call to SCR-FP-009's analytics CSV, which **is** written
 * here — because no contract has an analytics export route at all. The difference
 * is not a preference; it is whether the platform serves the document.
 *
 * ## It is gated by the same thing the screen is
 *
 * The path sits under `/billing`, so `resolveScreenRoute` claims it for SCR-FP-010
 * and `proxy.ts` — which runs on every request, not only on page navigations —
 * refuses it before this file runs for a caller whose seat does not carry that
 * screen. `getSession()` is checked again here, because a route handler that
 * assumed a guard upstream stops being guarded the day the guard moves.
 * fleet-billing-svc's own Owner-and-approved gate is the third check, and it is
 * the one that matters.
 *
 * ## A failure is a redirect, not a 500
 *
 * A problem here would otherwise be an error page inside a file the browser saved
 * as `.csv`. Sending the operator back to the screen with the same month puts the
 * failure where a `<ProblemPanel>` can explain it.
 */

export const dynamic = 'force-dynamic';

export async function GET(request: NextRequest): Promise<Response> {
  const session = await getSession();
  if (!session) return NextResponse.redirect(new URL('/login', request.url));

  const params = request.nextUrl.searchParams;
  const invoiceId = params.get('invoice')?.trim();
  if (!invoiceId) return NextResponse.redirect(new URL('/billing', request.url));

  const requested = params.get('format')?.trim().toLowerCase() ?? 'csv';
  // An unrecognised format is answered with the default rather than with a 400:
  // the service refuses one it does not serve, and this is a query string an
  // operator can end up editing by hand.
  const format: InvoiceExportFormat = isInvoiceExportFormat(requested) ? requested : 'csv';

  const back = new URL(`/billing?invoice=${encodeURIComponent(invoiceId)}`, request.url);

  let document;
  try {
    document = await download({
      org: invoiceExportTarget(invoiceId),
      searchParams: { format },
      accept: format === 'pdf' ? 'application/pdf' : 'text/csv',
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return NextResponse.redirect(back);
  }

  // The service names the file after the organisation's month
  // (`mageride-invoice-2026-06-{invoiceId}.csv`), "so a folder of downloads is
  // readable without opening them". Its name is used when it gave one.
  const filename = document.filename ?? `mageride-invoice-${invoiceId}.${format}`;

  return new NextResponse(document.bytes, {
    headers: {
      'content-type': document.contentType,
      'content-disposition': `attachment; filename="${filename}"`,
      // One organisation's bill under a per-caller evaluation. A shared cache
      // holding this keyed on the URL is one operator downloading another's.
      'cache-control': 'no-store, private',
    },
  });
}
