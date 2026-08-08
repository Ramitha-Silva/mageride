/**
 * Deny-by-default routing (AL-06), as a pure function over the menu the server
 * already filtered.
 *
 * The whole decision is: **a screen is reachable iff `GET /v1/admin/session`
 * returned its nav item.** That endpoint's menu is a projection of URD §2.3
 * evaluated by the same `IPermissionEvaluator` the API's own `RequireFeature`
 * gates use, so the portal never transcribes a role, a capability or a cell — the
 * one way "the UI is rendered from the same permission model the API enforces
 * server-side" (URD §2.2) can be true rather than aspirational.
 *
 * Three properties fall out of stating it that way:
 *
 *  - **A URL no permitted item claims is refused**, whether it is a screen this
 *    caller may not have or a path nobody has built. There is no allow branch.
 *  - **Hiding the nav entry and refusing the route are the same act**, so they
 *    cannot drift. A menu that lost an item and a guard that kept letting it
 *    through is the failure this shape makes unrepresentable.
 *  - **It is still not authorization.** AL-06 says so in as many words (US-21.1):
 *    the endpoint behind every screen is gated independently and answers 403 to a
 *    caller who guesses a path. What this stops is the console *offering* a screen
 *    whose every request would be refused.
 */

import type { AdminMenuGroup, AdminMenuItem, AdminSession } from '@/api/types';

import { normalisePath, PUBLIC_PATHS, resolveRoute, UNSCREENED_PATHS } from './routes';

export interface ResolvedScreen {
  readonly group: AdminMenuGroup;
  readonly item: AdminMenuItem;
}

/** Every permitted item, flattened, in menu order. */
export function permittedItems(menu: readonly AdminMenuGroup[]): ResolvedScreen[] {
  return menu.flatMap((group) => group.items.map((item) => ({ group, item })));
}

/** The permitted item a URL belongs to, or `null` when the caller may not have it. */
export function resolveScreen(
  menu: readonly AdminMenuGroup[],
  pathname: string,
): ResolvedScreen | null {
  const route = resolveRoute(pathname);
  if (!route) return null;

  return permittedItems(menu).find(({ item }) => item.key === route.key) ?? null;
}

/**
 * Whether the shell may render this URL at all.
 *
 * The two exempt sets are exempt for opposite reasons and neither is a hole:
 * {@link PUBLIC_PATHS} is the sign-in flow, which by definition has no permissions
 * to check; {@link UNSCREENED_PATHS} is `/` and the refusal page, which show the
 * caller nothing but a redirect and a "no".
 */
export function isReachable(menu: readonly AdminMenuGroup[], pathname: string): boolean {
  const path = normalisePath(pathname);
  if (PUBLIC_PATHS.includes(path) || UNSCREENED_PATHS.includes(path)) return true;
  return resolveScreen(menu, path) !== null;
}

/**
 * Where a caller lands after sign-in: their **first permitted screen**.
 *
 * Not `/dashboard`. URD §2.3 gives the Verification Officer ➖ on "Analytics &
 * reporting", so they have no dashboard at all and D2 §AP says as much —
 * "Verification Officer → onboarding queue only". Sending everyone to a fixed
 * route would greet exactly the role the screen was designed around with a 403 on
 * their first page.
 *
 * `null` when the caller holds no screen anywhere; the shell says so rather than
 * drawing an empty console (see `SessionEndpoints`' note on the same case).
 */
export function landingPath(session: AdminSession): string | null {
  return permittedItems(session.menu)[0]?.item.path ?? null;
}
