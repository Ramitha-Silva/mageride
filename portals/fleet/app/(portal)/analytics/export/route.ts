import { NextResponse, type NextRequest } from 'next/server';

import { read } from '@/api/client';
import {
  analyticsFileName,
  analyticsRange,
  colomboToday,
  csvRows,
  FLEET_ANALYTICS,
  type FleetAnalyticsAnswer,
} from '@/api/insights';
import { ProblemError } from '@/api/problem';
import { VEHICLES, type FleetVehicle, type FleetVehicleList } from '@/api/vehicles';
import { analyticsCsv } from '@/components/analytics/analytics-model';
import { getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-009's CSV export** — the same report the screen is showing, as a file.
 *
 * ## Why this is a route handler and not an API call
 *
 * `web_fleet.html` draws "Export CSV / PDF" and **no contract has an analytics
 * export route**: `exportFleetInvoice` is fleet-billing-svc's and produces one
 * month's invoice, which is a different document about different numbers. Rather
 * than draw a control that posts nowhere — or invent a platform capability — the
 * file is written here from the answer the screen already reads.
 *
 * It re-reads rather than receiving rows from the browser. A CSV built from a
 * request body would be a file whose contents the client chose; this one is
 * `GET /v1/fleets/{the caller's own id}/analytics` through the same `read()`, the
 * same bearer and the same row-level-security scope as the page, with the `from`
 * and `to` the page put in the link.
 *
 * ## It is gated by the same thing the screen is
 *
 * The path sits under `/analytics`, so `resolveScreenRoute` claims it for
 * SCR-FP-009 and `proxy.ts` — which runs on every request, not only on page
 * navigations — refuses it for a caller whose seat does not carry that screen,
 * before this file runs. `getSession()` is checked again here, because a route
 * handler that assumed a guard upstream is a route handler that stops being
 * guarded the day the guard moves.
 *
 * ## Failures are a redirect, not a 500
 *
 * A problem here means a download that produced an error page in a file the
 * browser saved as `.csv`. Sending the operator back to the screen with the same
 * range puts the failure where a `<ProblemPanel>` can explain it.
 */

export const dynamic = 'force-dynamic';

export async function GET(request: NextRequest): Promise<Response> {
  const session = await getSession();
  if (!session) return NextResponse.redirect(new URL('/login', request.url));

  const params = request.nextUrl.searchParams;
  const range = analyticsRange(
    { from: params.get('from') ?? undefined, to: params.get('to') ?? undefined },
    colomboToday(),
  );

  let answer: FleetAnalyticsAnswer;
  try {
    answer = await read<FleetAnalyticsAnswer>({
      org: FLEET_ANALYTICS,
      searchParams: { from: range.from, to: range.to },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return NextResponse.redirect(
      new URL(`/analytics?from=${range.from}&to=${range.to}`, request.url),
    );
  }

  const [t, roster] = await Promise.all([getTranslator(), rosterById()]);
  const body = csvRows(analyticsCsv(answer.items ?? [], range, roster, t));

  return new NextResponse(body, {
    headers: {
      // `charset=utf-8` **and** the BOM `csvRows` writes: the header is what a
      // browser and a text editor read, the BOM is what Excel reads, and a
      // Sinhala plate note needs both to survive the trip.
      'content-type': 'text/csv; charset=utf-8',
      'content-disposition': `attachment; filename="${analyticsFileName(range)}"`,
      // One organisation's figures under a per-caller evaluation. A shared cache
      // holding this keyed on the URL is one operator downloading another's.
      'cache-control': 'no-store, private',
    },
  });
}

async function rosterById(): Promise<ReadonlyMap<string, FleetVehicle>> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return new Map((answer.items ?? []).map((vehicle) => [vehicle.vehicleId, vehicle]));
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return new Map();
  }
}
