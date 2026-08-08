/**
 * Who may open which screen, and who may press which control — as pure functions
 * over the evaluation the **server** already made.
 *
 * Three facts decide everything, and the portal computes none of them:
 *
 *  1. **`permissions`** — the caller's rows of URD §2.3 from
 *     `GET /v1/me/permissions`, produced by `IPermissionEvaluator` with URD §2.1's
 *     fleet sub-model already applied. A Viewer's rows carry `read` and no
 *     `write`; a Manager's `fleet-billing` row carries `read` and no `write`. That
 *     narrowing happens in `MageRide.Shared.Auth`, and this file reads its output.
 *  2. **`fleetRole`** — the caller's seat in *this* organisation, read by iam-svc
 *     from `iam.fleet_members`. It is compared against the sub-role the owning
 *     route declares, which `./routes` transcribes from the endpoint metadata.
 *  3. **`organisation.status`** — US-13.A7's gate, as `registry.fleets` holds it.
 *
 * **None of this is authorization** (AL-06/US-21.1). `FleetAccessFilter` re-reads
 * the membership on every request and every endpoint re-decides; what these
 * functions stop is a console offering a screen or a button whose request would
 * be refused.
 */

import type { FleetPermission, FleetRole, FleetSession, PermissionGrant } from '@/api/types';

import {
  FLEET_NAV,
  normalisePath,
  PUBLIC_PATHS,
  resolveScreenRoute,
  UNSCREENED_PATHS,
  type FleetNavGroup,
  type FleetScreen,
} from './routes';

/* ---------------------------------------------------------------------------
 * The three primitives
 * ------------------------------------------------------------------------ */

/**
 * `FleetRoles.Rank` (`MageRide.Shared.Auth.MageRideClaims`), transcribed.
 *
 * An unknown value ranks below `viewer`, which is what makes a token from a
 * future release naming a sub-role this build has never heard of deny rather than
 * admit.
 */
const FLEET_ROLE_RANK: Readonly<Record<string, number>> = {
  owner: 3,
  manager: 2,
  viewer: 1,
};

/** `FleetRoles.Satisfies` — whether `held` is at least `required`. */
export function satisfiesFleetRole(held: FleetRole | null, required: FleetRole): boolean {
  const needed = FLEET_ROLE_RANK[required] ?? 0;
  return needed > 0 && (FLEET_ROLE_RANK[held ?? ''] ?? 0) >= needed;
}

/**
 * Whether the caller holds one capability in one URD §2.3 row, **as the server
 * evaluated it**.
 *
 * This reads a decision rather than making one: `permissions` is the caller's own
 * row computed by the same `IPermissionEvaluator` the endpoints gate on. There is
 * no `if (fleetRole === 'owner')` anywhere in this application and there must
 * never be one — {@link satisfiesFleetRole} compares against a value the *route*
 * declares, which is a different thing from re-deriving the matrix.
 */
export function holdsGrant(
  session: FleetSession,
  featureArea: string,
  grant: PermissionGrant,
): boolean {
  return row(session, featureArea)?.grants.includes(grant) ?? false;
}

function row(session: FleetSession, featureArea: string): FleetPermission | undefined {
  return session.permissions.find((permission) => permission.featureArea === featureArea);
}

/** US-13.A7 — has a Verification Officer approved the organisation yet? */
export function isApproved(session: FleetSession): boolean {
  return session.organisation?.status === 'APPROVED';
}

/* ---------------------------------------------------------------------------
 * Screens
 * ------------------------------------------------------------------------ */

/**
 * Why a screen is not open to this caller — or `null`, meaning it is.
 *
 * The two refusals are different pages because they are different facts and the
 * operator does two different things about them. `denied` is about the caller:
 * their seat does not carry this screen, and nothing they do today changes that.
 * `pending` is about the organisation: everybody in it is waiting on the same
 * Verification Officer, and the screen that explains it also says what is still
 * outstanding.
 */
export type Refusal = 'denied' | 'pending' | null;

export function refusalFor(session: FleetSession, screen: FleetScreen): Refusal {
  // No membership at all. One screen works in that state — the one that registers
  // an organisation — and every other is refused rather than rendered empty.
  if (!session.fleetId) {
    return screen.allowsNoOrganisation && holdsGrant(session, screen.area, screen.needs)
      ? null
      : 'denied';
  }

  if (!holdsGrant(session, screen.area, screen.needs)) return 'denied';
  if (!satisfiesFleetRole(session.fleetRole, screen.minimumFleetRole)) return 'denied';

  // Checked last, so a Viewer who may never open the vehicle screen is told that
  // rather than told to wait for an approval that would not help them.
  if (screen.requiresApprovedOrg && !isApproved(session)) return 'pending';

  return null;
}

/** Every screen this caller may open, in nav order. */
export function permittedScreens(session: FleetSession): FleetScreen[] {
  return FLEET_NAV.flatMap((group) => group.items).filter(
    (screen) => refusalFor(session, screen) === null,
  );
}

/** The nav as this caller sees it. A group left with no items is dropped with them. */
export function permittedNav(session: FleetSession): FleetNavGroup[] {
  return FLEET_NAV.map((group) => ({
    ...group,
    items: group.items.filter((screen) => refusalFor(session, screen) === null),
  })).filter((group) => group.items.length > 0);
}

/**
 * What the shell should do with a URL: render it, refuse it, explain the approval
 * gate, or answer 404.
 *
 * The two exempt sets are exempt for opposite reasons and neither is a hole:
 * {@link PUBLIC_PATHS} is the sign-in flow, which by definition has no permissions
 * to check; {@link UNSCREENED_PATHS} is `/` and the two refusal pages, which show
 * the caller nothing but a redirect and an explanation.
 */
export type Disposition = 'render' | 'denied' | 'pending' | 'not-found';

export function dispositionFor(session: FleetSession, pathname: string): Disposition {
  const path = normalisePath(pathname);
  if (PUBLIC_PATHS.includes(path) || UNSCREENED_PATHS.includes(path)) return 'render';

  const screen = resolveScreenRoute(path);
  if (!screen) return 'not-found';

  const refusal = refusalFor(session, screen);
  if (refusal === 'denied') return 'denied';
  if (refusal === 'pending') return 'pending';
  return 'render';
}

/**
 * Where a caller lands after sign-in.
 *
 * **The dashboard, once there is an approved organisation to have one of** — and
 * the first permitted screen otherwise, which for an organisation still in
 * verification is its own setup screen. A fixed `/dashboard` would greet a fleet
 * owner on their first day with counts of nothing, above a nav whose operating
 * half is not there yet; a plain "first permitted screen" would send an approved
 * operator to the organisation form every morning.
 *
 * `null` when the caller holds no screen anywhere — an account with the
 * `fleet_owner` role revoked, or one that never had it. The shell says so rather
 * than drawing an empty console.
 */
export function landingPath(session: FleetSession): string | null {
  const permitted = permittedScreens(session);

  if (isApproved(session)) {
    const dashboard = permitted.find((screen) => screen.key === 'dashboard');
    if (dashboard) return dashboard.path;
  }

  return permitted[0]?.path ?? null;
}

/* ---------------------------------------------------------------------------
 * Controls
 * ------------------------------------------------------------------------ */

/**
 * Whether a **mutating control** should be drawn at all.
 *
 * This is the Definition of Done item "a Viewer session renders no mutating
 * control anywhere", as one function every screen calls. A Viewer's rows carry no
 * `write` in any area — `PermissionEvaluator` restricts the whole `fleet_owner`
 * column to `Read | OwnScope` for them — so this is `false` for a Viewer
 * everywhere, without this file ever naming the sub-role.
 *
 * The approval gate is folded in for the same reason it is on the screens: a
 * button that is drawn, pressed and then answered `403 fleet-not-approved` has
 * told the operator nothing they could act on. Callers whose route is *not* inside
 * an approval-gated group pass `requiresApprovedOrg: false`.
 */
export function canMutate(
  session: FleetSession,
  featureArea: string,
  options: { readonly requiresApprovedOrg?: boolean } = {},
): boolean {
  if (!session.fleetId) return false;
  if (!holdsGrant(session, featureArea, 'write')) return false;
  if (options.requiresApprovedOrg && !isApproved(session)) return false;
  return true;
}

/**
 * Whether the caller may invite or change a team member (US-13.A5).
 *
 * `POST /v1/fleets/{id}/members` is `RequireFleetSubRole(Owner)` while the roster
 * beside it is Viewer, and URD §2.3 has no row that separates an Owner from a
 * Manager on `fleet-operations` — the sub-model narrows a Manager out of billing
 * and nothing else. So this is the one control gated on the seat as well as on
 * the row, and the seat is fleet-svc's own declaration rather than a rule this
 * portal invented. Recorded in the C111 handoff as the place the two models do
 * not line up.
 */
export function canManageTeam(session: FleetSession): boolean {
  return (
    canMutate(session, 'fleet-operations') && satisfiesFleetRole(session.fleetRole, 'owner')
  );
}
