'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';

/**
 * D2's "poll 2 s" on `GET …/uploads/{id}`, as a refresh of the screen rather than
 * a fetch of a fragment.
 *
 * ## Why it re-renders the page instead of fetching JSON
 *
 * The alternative — a client component holding the status, calling a JSON route
 * every two seconds and drawing the stepper itself — would need a second copy of
 * the view model in the browser, a second set of translated labels in the bundle,
 * and would still leave the version-history table showing the state of the world
 * from before the feed validated. `router.refresh()` re-runs the server render:
 * the stepper, the preview, the counts, the warnings **and** the history row all
 * move together, because they are one render of one payload, and the copy stays
 * where every other string on this screen is resolved.
 *
 * The cost is honest and bounded: two admin-bff reads every two seconds, for as
 * long as one feed is validating and somebody is watching it. When the verdict
 * lands, `active` goes false on the next render and the interval is torn down —
 * so a screen left open on a validated feed is doing nothing at all.
 *
 * ## Why polling exists when the payload is one request away
 *
 * Validation is a queued job (`GtfsValidationWorker`), deliberately outside the
 * request path — a national feed is half a million `stop_times` rows and BR-32.1
 * checks referential integrity across all of them. There is nothing to await; the
 * status endpoint is the only thing that knows, and the operator is watching a
 * stepper that must not sit still.
 */
export function FeedPoller({
  active,
  intervalMs = 2000,
}: {
  /** Whether the selected feed is still `uploaded` or `validating`. */
  active: boolean;
  intervalMs?: number;
}) {
  const router = useRouter();

  useEffect(() => {
    if (!active) return;

    const timer = setInterval(() => router.refresh(), intervalMs);
    return () => clearInterval(timer);
  }, [active, intervalMs, router]);

  return null;
}
