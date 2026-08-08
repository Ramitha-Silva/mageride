import { forbidden } from 'next/navigation';

/**
 * Where `proxy.ts` rewrites a request for a screen the caller's seat does not
 * permit.
 *
 * It exists because a status code is not something a React tree can set. The
 * proxy rewrites rather than redirects — so the operator stays on the URL they
 * asked for instead of watching their address bar be rewritten — and this page
 * calls `forbidden()`, which is what makes the response a real **403** carrying
 * `app/forbidden.tsx`'s body. A page that merely *said* "no" with a 200 would be
 * invisible to a bookmark checker, a link crawler and every test that asserts the
 * Definition of Done.
 */
export default function DeniedPage(): never {
  forbidden();
}
