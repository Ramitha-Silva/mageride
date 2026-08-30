/**
 * One navigation link, with everything the server could resolve already resolved.
 *
 * `href` and `label` come from the server; `path` stays because "am I the current
 * page?" is a `usePathname()` question and only the client can ask it. That split is
 * the whole shape of MCS-36 D3 in one interface: the strings cross the boundary, the
 * runtime state does not.
 */
export interface NavLink {
  /** The locale-relative path, for comparing against `usePathname()`. */
  readonly path: string;
  /** The fully-resolved href, built by the server from the route table. */
  readonly href: string;
  /** The route's label, already translated. */
  readonly label: string;
}
