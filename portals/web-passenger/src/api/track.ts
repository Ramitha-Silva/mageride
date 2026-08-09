import 'server-only';

import { apiFetch } from './http';
import { localProblem, ProblemError } from './problem';
import type {
  PickupResolution,
  PublicSos,
  Receipt,
  TrackEventBatch,
  TrackSnapshot,
} from './types';

/**
 * The typed data layer every SCR-WT screen reaches public-bff through.
 *
 * Six calls, one credential, and two fences neither a screen nor a route handler
 * can round.
 *
 * ## 1. The token is validated for **shape** before it is spent
 *
 * `public-bff.yaml` bounds the path parameter at 16–128 characters and
 * `ShareTokenMinter` produces 32 random bytes as base64url, so a value outside
 * `[A-Za-z0-9_-]{16,128}` was never minted. Refusing it here costs public-bff
 * nothing and buys two things: a junk link becomes SCR-WT-006 without a round
 * trip, and a probe cannot spend a real visitor's per-IP rate budget on this
 * deployment's behalf. It is also what makes {@link trackPath} safe — the token is
 * interpolated into a URL path, and a value that cannot contain `/`, `?`, `#` or a
 * percent sequence cannot be a path of its own.
 *
 * ## 2. Every path is built here, and nowhere else
 *
 * `trackPath()` is the only expression in the application that writes a share
 * token into a URL. `test/fences.test.ts` enumerates the tree and fails on a
 * second one, for the same reason the Fleet Portal allows exactly one place to
 * write a `{fleetId}`: a page that can compose the address can compose a different
 * one.
 */

/**
 * A token that could have been minted.
 *
 * Deliberately a *shape* test and not a claim about liveness — only
 * `safety.trip_share_tokens` knows that, and asking is the whole of
 * {@link readSnapshot}.
 */
const TOKEN_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;

export function isWellFormedToken(token: string | null | undefined): token is string {
  return typeof token === 'string' && TOKEN_PATTERN.test(token);
}

/**
 * The single place a share token becomes a URL.
 *
 * Throws the family's own `404 token-unknown` for a malformed value, so a caller
 * has one failure mode rather than two: every screen already routes a dead token
 * to SCR-WT-006, and "this was never a token" belongs on the same page as "this
 * token is over". Telling the two apart would make the surface an oracle over
 * which links exist.
 */
export function trackPath(token: string, suffix = ''): string {
  if (!isWellFormedToken(token)) {
    throw new ProblemError(
      localProblem('token-unknown', 404, '/public/track', 'The link is not a MageRide link.'),
    );
  }

  return `/public/track/${token}${suffix}`;
}

/**
 * `GET /public/track/{token}` — the snapshot, already shaped by the token's scope.
 *
 * **Which of the three shapes comes back is decided by the token, not by this
 * call.** There is no parameter that selects a variant and there must not be one:
 * a `pickup_confirm` holder asking differently must not obtain the package view,
 * and the service holds that by dispatching on the row. This side simply does not
 * offer a way to ask.
 */
export async function readSnapshot(token: string, signal?: AbortSignal): Promise<TrackSnapshot> {
  const { data } = await apiFetch<TrackSnapshot>({
    path: trackPath(token),
    ...(signal ? { signal } : {}),
  });
  return data;
}

/**
 * `GET /public/track/{token}/receipt` — SCR-WT-005's figures.
 *
 * `409 receipt-not-ready` is a normal answer rather than a failure: a parcel that
 * has been handed over but whose money has not settled is `Completed` or
 * `PaymentPending`, and public-bff's `Receiptable` is narrower than "terminal" on
 * purpose. The screen renders the delivery without the receipt block in that case,
 * so this returns `null` rather than throwing.
 */
export async function readReceipt(token: string, signal?: AbortSignal): Promise<Receipt | null> {
  try {
    const { data } = await apiFetch<Receipt>({
      path: trackPath(token, '/receipt'),
      ...(signal ? { signal } : {}),
    });
    return data;
  } catch (error) {
    if (error instanceof ProblemError && (error.status === 409 || error.code === 'receipt-not-ready')) {
      return null;
    }
    throw error;
  }
}

/**
 * `GET /public/track/{token}/live` as a **stream**, for the route handler that
 * proxies it to the browser's `EventSource`.
 *
 * The `Response` is handed back whole and its body is piped through untouched.
 * Reading it here would collect a five-minute feed into memory and deliver it when
 * it ended, which is the one thing a live feed must not do.
 */
export async function openLiveStream(
  token: string,
  lastEventId: string | null,
  signal?: AbortSignal,
): Promise<Response> {
  const { data } = await apiFetch<Response>({
    path: trackPath(token, '/live'),
    accept: 'text/event-stream',
    stream: true,
    // `Last-Event-ID` is what an `EventSource` sends on its own reconnect, and
    // public-bff honours it identically to `?since`. Forwarding it is the whole of
    // "the feed reconnects after a dropped stream without a full reload" on this
    // side: the browser resumes, the cursor travels with it, and the page is never
    // re-rendered.
    ...(lastEventId ? { headers: { 'last-event-id': lastEventId } } : {}),
    ...(signal ? { signal } : {}),
  });
  return data;
}

/**
 * `GET /public/track/{token}/live?since=…` — the same feed as a JSON batch, for a
 * client that cannot hold a socket open.
 *
 * One diff function and two transports on the service's side; one function and two
 * transports on this one. A page that behaved differently on a bad connection
 * would be behaving differently on the connection the fallback exists for.
 */
export async function pollLive(
  token: string,
  since: string,
  signal?: AbortSignal,
): Promise<TrackEventBatch> {
  const { data } = await apiFetch<TrackEventBatch>({
    path: trackPath(token, '/live'),
    searchParams: { since },
    ...(signal ? { signal } : {}),
  });
  return data;
}

/**
 * `POST /public/track/{token}/pickup/confirm` — SCR-WT-003's Share (US-25.3).
 *
 * Resolves the same `rides.location_requests` row the in-app
 * `POST /v1/location-requests/{id}/confirm` resolves, and the booker learns of it
 * over their SignalR group. `accuracy` is passed through when the browser
 * reported one and omitted when the pin was dragged — a metres figure that
 * described a fix the reader has since moved would be worse than no figure.
 */
export async function confirmPickup(
  token: string,
  point: { lat: number; lng: number; accuracy?: number },
): Promise<PickupResolution> {
  const { data } = await apiFetch<PickupResolution>({
    path: trackPath(token, '/pickup/confirm'),
    method: 'POST',
    body: {
      lat: point.lat,
      lng: point.lng,
      ...(point.accuracy === undefined ? {} : { accuracy: point.accuracy }),
    },
  });
  return data;
}

/**
 * `POST /public/track/{token}/pickup/decline` — SCR-WT-003's Decline (P-02).
 *
 * **It takes no coordinates and sends no body.** That is not a convenience: P-02's
 * rule is that declining transmits no GPS, and the fence is held by three
 * components at once — this function has no parameter for one, public-bff's
 * handler reads no body, and ride-svc's statement has no `resolved_geo` in its
 * `SET` list. A signature with an optional point would be the first of the three
 * to go.
 */
export async function declinePickup(token: string): Promise<PickupResolution> {
  const { data } = await apiFetch<PickupResolution>({
    path: trackPath(token, '/pickup/decline'),
    method: 'POST',
  });
  return data;
}

/**
 * `POST /public/track/{token}/sos` — SCR-WT-004's SOS (US-25.5, D-33).
 *
 * Dual-gateway SMS to the **booker** plus the admin live feed, recorded as
 * `safety.sos_events(source='web')`. The coordinates are the browser's, because
 * safety-svc's row "must say where the *person* said they were, not where the car
 * was" — the page's own fallback to the last driver-reported fix (D6' I-29.4) is
 * a choice the reader is told about on the screen, not a substitution made here.
 */
export async function raiseSos(
  token: string,
  point: { lat: number; lng: number; accuracy?: number },
): Promise<PublicSos> {
  const { data } = await apiFetch<PublicSos>({
    path: trackPath(token, '/sos'),
    method: 'POST',
    body: {
      lat: point.lat,
      lng: point.lng,
      ...(point.accuracy === undefined ? {} : { accuracy: point.accuracy }),
    },
  });
  return data;
}
