/**
 * Validates a `?next=` value before anything redirects to it.
 *
 * `?next=` is a redirect instruction that arrives in a query string, which means
 * it arrives from whoever wrote the link. Accepting an absolute URL would make the
 * Fleet Portal's sign-in screen an open redirect: a mail saying "your MageRide
 * session expired, sign in here" would land on the real portal, authenticate a
 * real fleet owner, and then hand them to somebody else's page — with the portal's
 * own domain in the address bar the whole way.
 *
 * So: a path, on this origin, and nothing else. `//evil.example` is rejected
 * explicitly because browsers read it as a scheme-relative **absolute** URL while
 * it still passes a naive `startsWith('/')`.
 */
export function safeNextPath(value: string | null | undefined): string | null {
  if (!value) return null;
  if (!value.startsWith('/')) return null;
  if (value.startsWith('//')) return null;
  // A backslash is normalised to a forward slash by some browsers, so `/\evil`
  // is the same trick wearing a different character.
  if (value.startsWith('/\\')) return null;
  if (value === '/') return null;
  return value;
}
