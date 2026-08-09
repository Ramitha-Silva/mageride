import { NextResponse, type NextRequest } from 'next/server';

import { isWellFormedToken, openLiveStream, pollLive } from '@/api/track';
import { ProblemError } from '@/api/problem';

/**
 * The **only** route handler on this surface: `GET /public/track/{token}/live`,
 * proxied same-origin so the page's `EventSource` never leaves
 * `passenger.mageride.lk`.
 *
 * ## Why proxy at all, when the token is already in the address bar
 *
 * Not to hide the credential — the reader is holding it. To keep the *platform* out
 * of the browser, which is the rule all three MageRide web surfaces hold: one
 * origin, one TLS handshake, no gateway address in any shipped script, no CORS
 * policy to widen on a no-login endpoint, and no second host for a captive portal
 * or a corporate proxy to break separately. `src/api/http.ts` is `server-only`, so
 * this is the only way out and there is no second one to add by accident.
 *
 * ## Both transports, because public-bff serves both from one diff
 *
 *   - **No `?since`** → SSE. The upstream body is piped through **untouched**:
 *     reading it would collect a five-minute feed into memory and deliver it when
 *     it ended, which is the one thing a live feed must not do. `Last-Event-ID` is
 *     forwarded, which is the whole of the reconnect — the browser reopens on its
 *     own and public-bff honours the header identically to `?since`, so C117's "the
 *     live feed reconnects after a dropped SSE stream without a full reload" needs
 *     no code on either side of this line.
 *   - **`?since=cursor`** → the JSON batch, for the connection SSE does not survive
 *     (an intermediary that buffers a response body, which no header of
 *     public-bff's can reach).
 *
 * ## A dead token is passed through as its status, not as a page
 *
 * 404 and 410 are answered with an empty body and the same code. The hook stops
 * polling and reloads; the **server** then re-reads the token and renders
 * SCR-WT-006. Nothing about the ride is written into this response, on any path —
 * not even a problem `detail`, which is English diagnostics this reader has no use
 * for.
 */

/** Never prerendered, never cached: this is a socket, and it is token-scoped. */
export const dynamic = 'force-dynamic';

/**
 * The Node runtime, not the edge.
 *
 * `MAGERIDE_API_BASE_URL` is read at **run time** so one image serves the replica
 * and DOKS (`src/config/env.ts`), and the upstream is a cluster-internal address
 * that an edge runtime has no route to.
 */
export const runtime = 'nodejs';

export async function GET(
  request: NextRequest,
  { params }: { params: Promise<{ token: string }> },
): Promise<Response> {
  const { token } = await params;

  if (!isWellFormedToken(token)) {
    return new NextResponse(null, { status: 404 });
  }

  const since = request.nextUrl.searchParams.get('since');

  try {
    if (since !== null) {
      const batch = await pollLive(token, since, request.signal);
      return NextResponse.json(batch, {
        headers: { 'cache-control': 'no-store' },
      });
    }

    const upstream = await openLiveStream(
      token,
      request.headers.get('last-event-id'),
      request.signal,
    );

    return new Response(upstream.body, {
      status: 200,
      headers: {
        'content-type': 'text/event-stream; charset=utf-8',
        'cache-control': 'no-cache, no-store, must-revalidate',
        connection: 'keep-alive',
        // Reverse proxies buffer by default, and a buffered SSE stream arrives all
        // at once when it ends. public-bff sets this on its own response; it has to
        // be set again here because this is a *new* response, and the proxy in
        // front of Next is a different one.
        'x-accel-buffering': 'no',
      },
    });
  } catch (error) {
    if (error instanceof ProblemError) {
      return new NextResponse(null, { status: error.status });
    }
    // Never reached through `apiFetch`, which turns a transport failure into a
    // `ProblemError` 503. A bare throw here would answer 500 with a Next error
    // page inside an `EventSource`, which the browser would retry for ever.
    return new NextResponse(null, { status: 503 });
  }
}
