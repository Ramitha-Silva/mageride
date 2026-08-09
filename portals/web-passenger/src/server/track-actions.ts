'use server';

import { confirmPickup, declinePickup, raiseSos } from '@/api/track';
import { isDeadToken, ProblemError, problemMessageKey } from '@/api/problem';
import type { WebMessageKey } from '@/i18n';

/**
 * The three writes this surface makes — SCR-WT-003's Share and Decline, and
 * SCR-WT-004's SOS.
 *
 * ## They are server actions, so the browser still never reaches the platform
 *
 * The token is in the visitor's address bar, so this is not about hiding a
 * credential; it is the same rule the other two portals hold for the same reason.
 * Everything MageRide leaves the Next server, `passenger.mageride.lk` is the only
 * host the phone talks to, and there is no gateway address in any script the page
 * ships. `src/api/http.ts` is `server-only`, so a client component that tried to
 * call public-bff directly would fail to compile.
 *
 * ## They return a **key**, never a sentence
 *
 * The Fleet Portal composes result copy inside its actions because its client
 * components cannot hold a translator. This surface's client components can — they
 * are handed a locale and build one (see `LiveTracker`) — so an action returns the
 * resource key and the component renders it. That keeps the action free of a
 * locale it would otherwise have to be told, and it keeps the two halves of an
 * outcome ("what happened" and "how it reads") on the right sides of the boundary.
 *
 * `problem.title` is never returned, in any form: `_shared.yaml` calls it a
 * developer's English summary and says it is never localised.
 *
 * ## A dead token is not an error, it is a screen
 *
 * A `404`/`410` on any of the three means the link is over — the five minutes
 * elapsed, the request was already answered, or safety-svc closed the token when
 * the trip ended. The action says so with `dead: true` and the component calls
 * `router.refresh()`; the **server** then re-reads the token and renders
 * SCR-WT-006, so the dead end is reached by the same path a fresh visit takes and
 * no ride data survives the transition.
 */

/** What a write answers with. Every field is a string or a boolean — it crosses a boundary. */
export interface WriteOutcome {
  readonly ok: boolean;
  /** The link is over; the caller refreshes and the server renders SCR-WT-006. */
  readonly dead?: boolean;
  /** A resource key for the failure. Absent on success. */
  readonly messageKey?: WebMessageKey;
  readonly traceId?: string;
}

export interface SosOutcome extends WriteOutcome {
  /** safety-svc's own vocabulary — `Dispatched`, `Failed` or `NoContact`. */
  readonly smsStatus?: 'Dispatched' | 'Failed' | 'NoContact';
}

/**
 * SCR-WT-003's **Share location** (US-25.3, AL-45).
 *
 * Resolves the same `rides.location_requests` row the in-app confirm resolves, so
 * "a web pickup-confirm resolves the same location request the app would" is a
 * property of which endpoint is called rather than of anything reimplemented here:
 * public-bff forwards to ride-svc's `/v1/internal/location-requests/{id}/confirm`,
 * which is the route ride-svc built for this caller, and the booker learns of it
 * over the same SignalR group.
 */
export async function sharePickupLocation(
  token: string,
  point: { lat: number; lng: number; accuracy?: number },
): Promise<WriteOutcome> {
  if (!Number.isFinite(point?.lat) || !Number.isFinite(point?.lng)) {
    // `JSON.stringify` turns a `NaN` into `null`, which public-bff would answer
    // `validation-failed` for — the same sentence, one round trip later.
    return { ok: false, messageKey: 'web.error.badLocation' };
  }

  try {
    await confirmPickup(token, {
      lat: point.lat,
      lng: point.lng,
      ...(Number.isFinite(point.accuracy) ? { accuracy: point.accuracy! } : {}),
    });
    return { ok: true };
  } catch (error) {
    return failure(error);
  }
}

/**
 * SCR-WT-003's **Decline** (P-02).
 *
 * **It takes a token and nothing else, and that is the fence rather than a
 * convenience.** "Declining never sends your GPS" is held by four components at
 * once: this signature has no parameter for a coordinate, `declinePickup` sends no
 * body, public-bff's handler reads none, and ride-svc's statement has no
 * `resolved_geo` in its `SET` list. Adding an optional point here would be the
 * first of the four to go, and the copy on the screen would quietly become untrue.
 */
export async function declinePickupLocation(token: string): Promise<WriteOutcome> {
  try {
    await declinePickup(token);
    return { ok: true };
  } catch (error) {
    return failure(error);
  }
}

/**
 * SCR-WT-004's **SOS** (US-25.5, D-33).
 *
 * Dual-gateway SMS to the **booker** plus the admin live feed, written as
 * `safety.sos_events(source='web')`. `smsStatus` is returned to the page because
 * without it the reader cannot tell "somebody has been told" from "this is on a
 * console in an office and nowhere else", and on a panic button that is the whole
 * difference.
 */
export async function raiseWebSos(
  token: string,
  point: { lat: number; lng: number; accuracy?: number },
): Promise<SosOutcome> {
  if (!Number.isFinite(point?.lat) || !Number.isFinite(point?.lng)) {
    return { ok: false, messageKey: 'web.error.badLocation' };
  }

  try {
    const raised = await raiseSos(token, {
      lat: point.lat,
      lng: point.lng,
      ...(Number.isFinite(point.accuracy) ? { accuracy: point.accuracy! } : {}),
    });
    return { ok: true, smsStatus: raised.smsStatus };
  } catch (error) {
    return failure(error);
  }
}

function failure(error: unknown): WriteOutcome {
  if (isDeadToken(error)) return { ok: false, dead: true };

  if (error instanceof ProblemError) {
    return {
      ok: false,
      messageKey: problemMessageKey(error.problem),
      ...(error.problem.traceId ? { traceId: error.problem.traceId } : {}),
    };
  }

  // Anything that is not a `ProblemError` never reached public-bff and is this
  // process's own fault. The reader gets one sentence; the stack stays in the log.
  return { ok: false, messageKey: 'web.error.unexpected' };
}
