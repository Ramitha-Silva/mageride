'use server';

import { revalidatePath } from 'next/cache';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import {
  feedLabel,
  gtfsActivatePath,
  isFeedVersionId,
  type FeedVersion,
} from '@/api/transit';
import { getTranslator } from '@/i18n/server';

/**
 * SCR-AP-016's one write: `POST /v1/admin/transit/gtfs/uploads/{id}/activate`
 * (US-28.2, and US-28.3's rollback, which is the same call).
 *
 * ## Why the upload is not here
 *
 * A server action's body arrives as a serialised `FormData` and Next caps it well
 * below 200 MB — but the real reason is progress. D2 asks for a progress bar while
 * a national feed uploads, and neither a server action nor `fetch` can report
 * upload progress at all; only `XMLHttpRequest` can. So the upload is an XHR
 * against a route handler that streams the body through (`app/(portal)/config/
 * transit/gtfs/upload/route.ts`), and this file is left with the small POST.
 *
 * ## Why the swap is safe to offer as one button
 *
 * transit-svc loads `transit_staging.gtfs_*` from the stored zip and then swaps
 * the live tables in **one transaction** — `ALTER TABLE … SET SCHEMA`, a catalogue
 * update that rewrites no row — so the dataset is replaced in the time it takes to
 * take the locks, whatever the feed's size. The staging load touches only
 * `transit_staging.*`; nothing touches `transit.*` outside the swap. **A failed
 * activation therefore leaves the previous feed live**, which is a property of
 * which tables each phase writes rather than of anything unwinding, and it is what
 * lets this action report a failure without also having to say what state the
 * platform was left in.
 *
 * ## The idempotency key is fresh on every press, deliberately
 *
 * BR-32.2 makes activation idempotent on `Idempotency-Key` and transit-svc keeps a
 * command log to honour it — which is exactly why a **stable** key would be wrong
 * here. Rollback is activation, so `v3 → v2 → v3` is a legitimate sequence, and a
 * key derived from the feed version id would make the third press replay the first
 * one's response out of the log without swapping anything. A double click is
 * bounded instead by the button, which is disabled while the request is in flight,
 * and by the advisory lock behind it: the second request either serialises and is
 * answered `409 feed-already-active`, or waits and is answered `409 conflict`.
 * Both are refusals the operator can read, and neither swaps twice.
 */

export interface ActivateFeedState {
  /** A refusal, already resolved to the operator's language. */
  readonly message?: string;
  /** Set on success — what the toast names, and what the screen re-reads. */
  readonly activated?: {
    readonly feedVersionId: string;
    readonly label: string;
  };
}

export async function activateFeed(
  _state: ActivateFeedState,
  formData: FormData,
): Promise<ActivateFeedState> {
  const t = await getTranslator();

  const raw = formData.get('feedVersionId');
  const feedVersionId = typeof raw === 'string' ? raw.trim() : '';

  // The id is put into a path this process builds, and it comes from a hidden
  // field. transit-svc routes on `{feedVersionId:guid}` and would refuse anything
  // else; checking here means the refusal never depends on that.
  if (!isFeedVersionId(feedVersionId)) return { message: t('admin.error.unexpected') };

  // `| undefined` because a 200 with an empty body is a shape the transport can
  // hand back, and a toast is not worth a TypeError.
  let version: FeedVersion | undefined;
  try {
    const outcome = await mutate<FeedVersion>({
      method: 'POST',
      path: gtfsActivatePath(feedVersionId),
      // The row an auditor will find is transit-svc's, written inside the swap
      // transaction. See `TransitAuditAction` for why this is not the C108
      // `auditedElsewhere` case: a row is written, by the service that owns the
      // tables being renamed.
      audit: {
        action: 'GTFS_FEED_ACTIVATED',
        entity: 'gtfs_feed',
        entityId: feedVersionId,
      },
    });
    version = outcome.data;
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    // `feed-not-validated`, `feed-already-active` and `conflict` each have their
    // own sentence in `MESSAGE_KEYS`; anything else falls through to the shell's.
    return { message: t(error.messageKey) };
  }

  revalidatePath('/config/transit/gtfs');

  return {
    activated: {
      feedVersionId,
      // From the response rather than from the row the screen rendered: activation
      // is the moment `feed_info` version becomes the *live* one, and the toast
      // says which feed passengers are now being routed on.
      label: feedLabel(version ?? { feedVersionId, feedInfoVersion: null }),
    },
  };
}
