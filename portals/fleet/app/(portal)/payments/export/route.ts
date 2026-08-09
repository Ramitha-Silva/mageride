import { NextResponse, type NextRequest } from 'next/server';

import { read } from '@/api/client';
import { csvRows } from '@/api/insights';
import { ProblemError } from '@/api/problem';
import {
  byNewestPeriod,
  paymentsFileName,
  subscriberPaymentsTarget,
  vehicleSubscribersTarget,
  LEDGER_PAGE_LIMIT,
  SUBSCRIPTION_PAGE_LIMIT,
  type SubscriberPage,
  type SubscriptionPaymentPage,
} from '@/api/subscriptions';
import { VEHICLES, type FleetVehicleList } from '@/api/vehicles';
import { paymentsCsv } from '@/components/subscriptions/subscription-model';
import { getLocale, getTranslator } from '@/i18n/server';
import { canManageSubscribers } from '@/server/access';
import { getSession } from '@/server/session';

/**
 * **SCR-FP-012's CSV export** — the ledger the screen is showing, as a file
 * (US-23.10, "with summary KPIs … and export").
 *
 * ## Why this is a route handler and not an API call
 *
 * No contract has a subscription-payment export. `fleet.yaml`'s Epic 23 block is
 * eight proxies and none of them renders a document; subscription-svc's only
 * document route is the HMAC-signed slip/QR file. So the file is written here from
 * the same answer the screen reads — the same arrangement SCR-FP-009's analytics
 * CSV has, and deliberately not SCR-FP-010's, where fleet-billing-svc renders both
 * formats and `download()` streams them.
 *
 * It **re-reads** rather than taking rows from the browser: a CSV built from a
 * request body would be a file whose contents the client chose. This one goes
 * through the same `read()`, the same bearer and the same row-level-security scope
 * as the page, for the vehicle and subscriber in the link.
 *
 * ## It is gated three times, and this file is the third
 *
 * The path sits under `/payments`, so `resolveScreenRoute` claims it for
 * SCR-FP-012 and `proxy.ts` — which runs on every request, not only on page
 * navigations — refuses it for a caller whose seat does not carry that screen. The
 * nav does not offer it either. `canManageSubscribers()` is checked again here,
 * because a route handler that assumed a guard upstream is a route handler that
 * stops being guarded the day the guard moves.
 *
 * ## Failures are a redirect, not a 500
 *
 * A problem here means a download that produced an error page in a file the
 * browser saved as `.csv`. Sending the operator back to the screen with the same
 * scope puts the failure where a `<ProblemPanel>` can explain it.
 */

export const dynamic = 'force-dynamic';

export async function GET(request: NextRequest): Promise<Response> {
  const session = await getSession();
  if (!session) return NextResponse.redirect(new URL('/login', request.url));

  const params = request.nextUrl.searchParams;
  const vehicleId = params.get('vehicle') ?? '';
  const subscriberId = params.get('subscriber') ?? '';

  const back = new URL(
    `/payments?vehicle=${encodeURIComponent(vehicleId)}&subscriber=${encodeURIComponent(subscriberId)}`,
    request.url,
  );

  if (!canManageSubscribers(session) || !vehicleId || !subscriberId) {
    return NextResponse.redirect(back);
  }

  let ledger: SubscriptionPaymentPage;
  try {
    ledger = await read<SubscriptionPaymentPage>({
      org: subscriberPaymentsTarget(vehicleId, subscriberId),
      searchParams: { limit: LEDGER_PAGE_LIMIT },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return NextResponse.redirect(back);
  }

  const [t, locale, plate, subscriber] = await Promise.all([
    getTranslator(),
    getLocale(),
    vehiclePlate(vehicleId),
    subscriberLabel(vehicleId, subscriberId),
  ]);

  const payments = [...(ledger.items ?? [])].sort(byNewestPeriod);
  const body = csvRows(
    paymentsCsv(
      payments,
      {
        vehicle: plate ?? vehicleId,
        subscriber: subscriber ?? subscriberId,
      },
      locale,
      t,
    ),
  );

  return new NextResponse(body, {
    headers: {
      // `charset=utf-8` **and** the BOM `csvRows` writes: the header is what a
      // browser and a text editor read, the BOM is what Excel reads, and a
      // Sinhala subscriber name needs both to survive the trip.
      'content-type': 'text/csv; charset=utf-8',
      'content-disposition': `attachment; filename="${paymentsFileName(plate ?? 'vehicle', subscriberId)}"`,
      // One organisation's money under a per-caller evaluation. A shared cache
      // holding this keyed on the URL is one operator downloading another's.
      'cache-control': 'no-store, private',
    },
  });
}

/** The plate, for the file's own first column. `null` if the roster is unreadable. */
async function vehiclePlate(vehicleId: string): Promise<string | null> {
  try {
    const answer = await read<FleetVehicleList>({ org: VEHICLES });
    return (
      (answer.items ?? []).find((vehicle) => vehicle.vehicleId === vehicleId)
        ?.registrationNumber ?? null
    );
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}

/** The subscriber's name as the roster gives it. `null` falls back to the id. */
async function subscriberLabel(vehicleId: string, subscriberId: string): Promise<string | null> {
  try {
    const answer = await read<SubscriberPage>({
      org: vehicleSubscribersTarget(vehicleId),
      searchParams: { limit: SUBSCRIPTION_PAGE_LIMIT },
    });
    return (
      (answer.items ?? []).find((row) => row.subscriberId === subscriberId)?.name?.trim() ?? null
    );
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return null;
  }
}
