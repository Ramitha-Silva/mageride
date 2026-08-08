/**
 * The portal's own route table: which **screen** a URL belongs to.
 *
 * This is emphatically *not* a copy of URD §2.3. It carries no roles and no
 * capabilities — only the fact that `/verification/expiring` is the
 * document-expiry screen and not a sub-page of the verification queue. Who may
 * open either is decided by admin-bff and arrives on `GET /v1/admin/session` as
 * the filtered menu; see `./access.ts`.
 *
 * **It has to exist, and the reason is a leak.** Deny-by-default over the menu
 * alone means "a path is reachable if a permitted item's path is a prefix of it".
 * The moment two items are nested — `/verification` and `/verification/expiring` —
 * that rule hands the second screen to anyone who holds the first, whatever the
 * matrix says about it. Knowing every screen's path is what lets a more specific
 * route out-rank a less specific one, so a nested screen is checked on **its own**
 * entry or not at all.
 *
 * Keys and paths mirror `MageRide.AdminBff.Navigation.AdminMenu.All`
 * (`backend/src/AdminBff/Navigation/AdminMenu.cs`). `test/routes.test.ts` parses
 * that file and asserts the two agree, so a nav item added, moved or renamed there
 * fails this build rather than quietly becoming a 403 nobody can explain.
 *
 * **This table reaches the browser**, because `SideNav` needs it to decide which
 * entry is the current page and `SideNav` is a client component. That is fine and
 * deliberate: it is twenty-five paths, all of them printed in D2 §AP and drawn in
 * `specs/wireframes/web_admin.html`, and none of them says who may open it. The
 * caller's own menu — the only thing that answers that — is resolved on the
 * server and reaches the client already filtered.
 */

export interface AdminRoute {
  /** The `AdminMenuItem.key` this route belongs to. */
  readonly key: string;
  /** The route prefix. A URL is this screen's if it is the path or sits under it. */
  readonly path: string;
}

export const ADMIN_ROUTES: readonly AdminRoute[] = [
  { key: 'dashboard', path: '/dashboard' },
  { key: 'audit-log', path: '/audit-log' },

  { key: 'verification', path: '/verification' },
  { key: 'document-expiry', path: '/verification/expiring' },

  { key: 'passengers', path: '/passengers' },
  { key: 'drivers', path: '/drivers' },
  { key: 'vehicles', path: '/vehicles' },

  { key: 'reports', path: '/reports' },
  { key: 'support-tickets', path: '/support/tickets' },
  { key: 'fraud-review', path: '/moderation/fraud' },

  { key: 'reconciliation', path: '/finance/reconciliation' },
  { key: 'transactions', path: '/finance/transactions' },
  { key: 'refunds', path: '/finance/refunds' },
  { key: 'wallet-adjustments', path: '/finance/adjustments' },
  { key: 'pdpa', path: '/pdpa' },

  { key: 'fare-tariffs', path: '/config/fares' },
  { key: 'cities', path: '/config/cities' },
  { key: 'feature-flags', path: '/config/feature-flags' },
  { key: 'trains', path: '/config/trains' },
  { key: 'announcements', path: '/announcements' },
  { key: 'gtfs', path: '/config/transit/gtfs' },
  { key: 'daily-fee-rates', path: '/config/fees' },
  { key: 'voucher-tiers', path: '/config/voucher-tiers' },
  { key: 'driver-levels', path: '/config/driver-levels' },

  { key: 'rbac', path: '/access/users' },
];

/**
 * Routes served without a session: the sign-in screen and the two legs of the
 * Google authorization-code round trip. Nothing else in the application is
 * reachable signed out.
 */
export const PUBLIC_PATHS: readonly string[] = ['/login', '/auth/callback', '/auth/google'];

/**
 * Routes that need a session but belong to no screen, so the AL-06 route check has
 * nothing to check them against.
 *
 * `/` is a redirect and nothing else: it resolves the caller's **first permitted
 * screen** and sends them there, which is the only landing rule that works for a
 * Verification Officer — URD §2.3 gives them ➖ on "Analytics & reporting", so they
 * have no dashboard to land on at all (D2 §AP: "Verification Officer → onboarding
 * queue only"). `/denied` is where a refused route is rewritten to, and gating the
 * refusal page would be a loop.
 */
export const UNSCREENED_PATHS: readonly string[] = ['/', '/denied'];

/** Strips a trailing slash so `/dashboard/` and `/dashboard` are one route. */
export function normalisePath(pathname: string): string {
  if (pathname.length > 1 && pathname.endsWith('/')) return pathname.replace(/\/+$/, '');
  return pathname || '/';
}

/** Whether `pathname` is the route at `routePath`, or sits under it. */
export function covers(routePath: string, pathname: string): boolean {
  return pathname === routePath || pathname.startsWith(`${routePath}/`);
}

/**
 * The screen a URL belongs to — the **longest** matching entry, so a nested screen
 * wins over its parent. `null` for a URL no screen claims.
 */
export function resolveRoute(pathname: string): AdminRoute | null {
  const path = normalisePath(pathname);

  let best: AdminRoute | null = null;
  for (const route of ADMIN_ROUTES) {
    if (!covers(route.path, path)) continue;
    if (!best || route.path.length > best.path.length) best = route;
  }
  return best;
}
