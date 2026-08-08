'use client';

import { ErrorPanel } from '@/components/ErrorPanel';

/**
 * The boundary for anything that throws below the root layout — including the
 * `503` `ProblemError` `getSession()` deliberately does not swallow when the
 * gateway is unreachable.
 *
 * That distinction is the point of letting it get this far: a platform outage
 * reads as an outage with a Retry, rather than as a silent bounce to a sign-in
 * screen where the operator's password would not have helped.
 */
export default function ErrorBoundary({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return <ErrorPanel {...(error.digest ? { digest: error.digest } : {})} onRetry={reset} />;
}
