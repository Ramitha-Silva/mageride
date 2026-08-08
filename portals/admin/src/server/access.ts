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

import type {
  AdminMenuGroup,
  AdminMenuItem,
  AdminSession,
  PermissionGrant,
} from '@/api/types';

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
 * Whether the caller holds one capability in one URD §2.3 row, **as the server
 * evaluated it** (Δ C108).
 *
 * This is not a second copy of the matrix and not a role check. `permissions` on
 * `GET /v1/admin/session` is the caller's own row computed by the same
 * `IPermissionEvaluator` the endpoints gate on — the shell's second load-bearing
 * decision is that the portal never decides who may see what, and this reads the
 * decision rather than making one. There is still no `if (role === …)` anywhere.
 *
 * **It is needed because a nav item is coarser than a control.** The refund queue
 * is one screen with two audiences: URD §2.3's Refunds row gives a Support CSR
 * `◐ raise/recommend` and a Finance Officer `✅ approve/execute`, so both reach
 * `/finance/refunds` and only one may execute. Deciding that from the menu alone
 * is impossible — the item is the same item — and drawing the form for both would
 * offer the CSR a button admin-bff answers 403 on.
 *
 * **And it is still not authorization** (AL-06/US-21.1): `POST /finance/refunds`
 * re-decides on `Refunds · Write` and is the only thing that matters. What this
 * stops is a console offering a control whose request would be refused.
 *
 * **`ownScope` is deliberately not consulted**, and the reason is that it cannot
 * answer the question. It is `ScopedGrants != None` — *some* capability in the row
 * is limited to the caller's own records — and the payload does not say which, so a
 * caller holding two roles (US-21.4's union) could have platform-wide `write` from
 * one and own-scope `raise` from the other and be refused a control they hold.
 * admin-bff makes the precise check with `RequiresOwnScope(needed)` against
 * `ScopedGrants`, which its session response collapses to a boolean; this function
 * therefore takes the coarse reading, and `test/finance-access.test.ts` parses URD
 * §2.3 to assert that no internal role holds a scope-limited `write` in a row this
 * console gates a control on — so the coarse reading and the precise one agree
 * today, and the build fails on the day they stop.
 */
export function holdsGrant(
  session: AdminSession,
  featureArea: string,
  grant: PermissionGrant,
): boolean {
  const row = session.permissions.find((permission) => permission.featureArea === featureArea);
  return row?.grants.includes(grant) ?? false;
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
