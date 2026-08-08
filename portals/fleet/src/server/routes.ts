import type { FleetRole, PermissionGrant } from '@/api/types';
import type { FleetMessageKey } from '@/i18n';

/**
 * The Fleet Portal's screen manifest: every URL the shell can render, what it is
 * called, and the three declarations that decide who reaches it.
 *
 * ## Why this table exists at all, when the Admin Portal has no such thing
 *
 * `GET /v1/admin/session` hands the Admin Portal a menu admin-bff has **already
 * filtered**, so C104's rule is "a screen is reachable iff its nav item is in that
 * menu" and the portal never decides anything. **fleet-svc sends no menu.** There
 * is no route on any contract that answers "which Fleet Portal screens may this
 * caller open", so the manifest has to live somewhere, and this is it.
 *
 * What that does *not* mean is that the portal decides who may see what. Each
 * entry declares the gate **its own routes declare**, and the three declarations
 * are read back out of the caller's server-side evaluation rather than derived
 * here:
 *
 *  - `area` + `needs` — the URD §2.3 row the screen's endpoints are gated on
 *    (`RequireFeature`). The answer comes from `GET /v1/me/permissions`, which is
 *    `IPermissionEvaluator`'s output with URD §2.1's fleet sub-model already
 *    applied — so a Viewer's rows carry `read` and no `write` because iam-svc
 *    said so, not because this portal knows what a Viewer is.
 *  - `minimumFleetRole` — the sub-role the owning service's own route declares
 *    with `RequireFleetSubRole(...)`. That is metadata on an endpoint, not a
 *    permission rule; mirroring it is transcription, and `test/routes.test.ts`
 *    parses the C# and fails when the two disagree.
 *  - `requiresApprovedOrg` — US-13.A7's gate, and it is set on exactly the
 *    screens whose routes sit inside a group carrying `RequireApprovedFleet()`.
 *    Not one screen more: refusing something fleet-svc allows would be this
 *    portal inventing a refusal, and the same test parses `FleetEndpoints.cs` and
 *    `FleetOpsEndpoints.cs` to hold the two together.
 *
 * **It is still not authorization** (AL-06/US-21.1): every endpoint re-decides,
 * and `FleetAccessFilter` re-reads the caller's seat from `iam.fleet_members` on
 * every request. What this stops is a console offering a screen whose every
 * request would be refused.
 *
 * ## Paths
 *
 * The paths are `specs/wireframes/web_fleet.html`'s own address bars, which is
 * where SCR-FP-001…012 are drawn. `/vehicles/onboard` in the wireframe is the
 * onboarding tab of the vehicles screen; the nav entry is the prefix, so a nested
 * screen a sibling component adds later resolves to it without a change here.
 */

/** One screen the portal can render. */
export interface FleetScreen {
  /** Stable key. The nav highlight, the placeholder and the tests all name it. */
  readonly key: string;
  readonly labelKey: FleetMessageKey;
  /** Route prefix. A URL is this screen's if it is the path or sits under it. */
  readonly path: string;
  /** The URD §2.3 row this screen's endpoints are gated on. */
  readonly area: string;
  /** The capability those endpoints need in that row. */
  readonly needs: PermissionGrant;
  /** The sub-role the owning service declares with `RequireFleetSubRole`. */
  readonly minimumFleetRole: FleetRole;
  /** Whether the routes behind it sit inside a `RequireApprovedFleet()` group. */
  readonly requiresApprovedOrg: boolean;
  /**
   * Whether the screen works for an account that holds `fleet_owner` and belongs
   * to no organisation yet. True for exactly one screen — the one that registers
   * it (`POST /v1/fleets` is the only fleet route with no `{fleetId}` in it).
   */
  readonly allowsNoOrganisation?: boolean;
  /** Which service answers this screen's API. Three of the four are not fleet-svc. */
  readonly ownedBy: string;
  /** The wireframe id, so a placeholder can say which screen is missing. */
  readonly screen: string;
}

export interface FleetNavGroup {
  readonly key: string;
  readonly labelKey: FleetMessageKey;
  readonly items: readonly FleetScreen[];
}

/**
 * The nav, in the order `web_fleet.html` draws it.
 *
 * **Mode C appears nowhere and cannot (AL-03).** There is no on-demand group, no
 * dispatch screen and no ride queue: a fleet operates Mode A and/or Mode B, the
 * `registry.fleet_vehicles.mode` CHECK admits `A` and `B`, and a driver's own
 * standby vehicle is onboarded in the Driver App. `test/fences.test.ts` asserts
 * the absence against the whole tree.
 */
export const FLEET_NAV: readonly FleetNavGroup[] = [
  {
    key: 'setup',
    labelKey: 'fleet.nav.group.setup',
    items: [
      {
        key: 'organisation',
        labelKey: 'fleet.nav.organisation',
        path: '/org/setup',
        area: 'fleet-operations',
        needs: 'read',
        minimumFleetRole: 'viewer',
        requiresApprovedOrg: false,
        // `POST /v1/fleets` carries no `{fleetId}` and is gated on the canonical
        // `fleet_owner` role alone — "the one route with no organisation to be
        // scoped to yet" (FleetEndpoints). It is therefore the only screen a
        // signed-in account with no membership can reach.
        allowsNoOrganisation: true,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-002',
      },
      {
        key: 'payout',
        labelKey: 'fleet.nav.payout',
        path: '/org/payout',
        // The organisation's bank account. `fleet-billing` is the row URD §2.1
        // narrows a Manager out of, and `write` in it is held by the Owner alone
        // — which is the same answer fleet-svc's `RequireFleetSubRole(Owner)`
        // gives on all three payout-profile routes, reached from the matrix
        // rather than from a role name.
        area: 'fleet-billing',
        needs: 'write',
        minimumFleetRole: 'owner',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-002a',
      },
      {
        key: 'team',
        labelKey: 'fleet.nav.team',
        path: '/org/team',
        area: 'fleet-operations',
        needs: 'read',
        // `GET …/members` is Viewer; `POST …/members` is Owner. The *screen* is
        // the roster, so the nav entry takes the read gate and the invite control
        // takes the write one — `canManageTeam()` in `./access`.
        minimumFleetRole: 'viewer',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-002',
      },
    ],
  },
  {
    key: 'operate',
    labelKey: 'fleet.nav.group.operate',
    items: [
      {
        key: 'dashboard',
        labelKey: 'fleet.nav.dashboard',
        path: '/dashboard',
        area: 'fleet-monitoring',
        needs: 'read',
        minimumFleetRole: 'viewer',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-003',
      },
      {
        key: 'vehicles',
        labelKey: 'fleet.nav.vehicles',
        path: '/vehicles',
        area: 'fleet-operations',
        needs: 'read',
        minimumFleetRole: 'viewer',
        // `FleetEndpoints.FleetVehiclesGroup` carries `RequireApprovedFleet()`,
        // and the whole group does — the list as well as the writes. A pending
        // organisation cannot read its own vehicles, so the screen is not shown.
        requiresApprovedOrg: true,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-004',
      },
      {
        key: 'drivers',
        labelKey: 'fleet.nav.drivers',
        path: '/drivers',
        area: 'fleet-operations',
        needs: 'read',
        minimumFleetRole: 'viewer',
        // `FleetEndpoints.FleetAssignmentsGroup`, same gate.
        requiresApprovedOrg: true,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-005',
      },
      {
        key: 'trackers',
        labelKey: 'fleet.nav.trackers',
        path: '/trackers',
        area: 'tracker-binding',
        needs: 'read',
        minimumFleetRole: 'viewer',
        // Deliberately false. `POST …/trackers/bind` carries the approval gate on
        // itself, but the health rollup this screen reads
        // (`GET …/health`, fleet-health-svc) does not — so the screen opens for a
        // pending org and the binding control is what is refused. Blocking the
        // screen would refuse a read the platform allows.
        requiresApprovedOrg: false,
        ownedBy: 'fleet-health-svc',
        screen: 'SCR-FP-006',
      },
      {
        key: 'map',
        labelKey: 'fleet.nav.map',
        path: '/map',
        area: 'fleet-monitoring',
        needs: 'read',
        minimumFleetRole: 'viewer',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-007',
      },
    ],
  },
  {
    key: 'manage',
    labelKey: 'fleet.nav.group.manage',
    items: [
      {
        key: 'scheduling',
        labelKey: 'fleet.nav.scheduling',
        path: '/scheduling',
        area: 'fleet-operations',
        needs: 'read',
        minimumFleetRole: 'viewer',
        // `GET …/schedules` is ungated; `POST …/schedules` carries the approval
        // gate on itself. Same shape as trackers.
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-008',
      },
      {
        key: 'analytics',
        labelKey: 'fleet.nav.analytics',
        path: '/analytics',
        area: 'fleet-monitoring',
        needs: 'read',
        minimumFleetRole: 'viewer',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-svc',
        screen: 'SCR-FP-009',
      },
      {
        key: 'billing',
        labelKey: 'fleet.nav.billing',
        path: '/billing',
        // Δ **The service is stricter than the matrix, and the portal follows the
        // service.** URD §2.1 narrows a Manager out of *writing* here and leaves
        // the read; `fleet-billing.yaml` says in its own preamble that "every
        // route under `/v1/fleets/{fleetId}` here — reads included — requires the
        // `owner` sub-role". Showing a Manager a Billing entry whose every request
        // answers 403 is worse than not showing it. Raised in the C111 handoff.
        area: 'fleet-billing',
        needs: 'write',
        minimumFleetRole: 'owner',
        requiresApprovedOrg: false,
        ownedBy: 'fleet-billing-svc',
        screen: 'SCR-FP-010',
      },
    ],
  },
  {
    key: 'subscribers',
    labelKey: 'fleet.nav.group.subscribers',
    items: [
      {
        key: 'subscriptions',
        labelKey: 'fleet.nav.subscriptions',
        path: '/subscriptions',
        area: 'fleet-operations',
        // The request queue and the roster are `RequireFleetSubRole(Manager)`,
        // and `write` on `fleet-operations` is what a Manager holds and a Viewer
        // does not.
        needs: 'write',
        minimumFleetRole: 'manager',
        // The proxy group is built on `FleetVehiclesGroup` and inherits its
        // approval gate — "an unapproved organisation has no subscribers to
        // manage" (FleetOpsEndpoints).
        requiresApprovedOrg: true,
        ownedBy: 'subscription-svc',
        screen: 'SCR-FP-011',
      },
      {
        key: 'payments',
        labelKey: 'fleet.nav.payments',
        path: '/payments',
        area: 'fleet-billing',
        needs: 'write',
        // `…/subscribers/{id}/payments` and `…/payments/{id}/confirm` are both
        // `RequireFleetSubRole(Owner)` — US-23.6, "only the fleet Owner can mark
        // it received".
        minimumFleetRole: 'owner',
        requiresApprovedOrg: true,
        ownedBy: 'subscription-svc',
        screen: 'SCR-FP-012',
      },
    ],
  },
];

/** Every screen, flattened, in nav order. */
export const FLEET_SCREENS: readonly FleetScreen[] = FLEET_NAV.flatMap((group) => group.items);

/**
 * Routes served without a session: the sign-in screen, the two federated sign-in
 * legs and their callbacks. Nothing else in the application is reachable signed
 * out.
 *
 * The provider legs are enumerated rather than matched by prefix. `/auth/**` as a
 * public prefix would make any future route under it public by accident, and this
 * is the one list where an accident is a hole.
 */
export const PUBLIC_PATHS: readonly string[] = [
  '/login',
  '/auth/google',
  '/auth/apple',
  '/auth/callback/google',
  '/auth/callback/apple',
];

/**
 * Routes that need a session but belong to no screen, so the screen check has
 * nothing to check them against.
 *
 * `/` is a redirect and nothing else. `/denied` is where a refused route is
 * rewritten to, and `/pending` is where an approval-gated one is — gating either
 * would be a loop.
 */
export const UNSCREENED_PATHS: readonly string[] = ['/', '/denied', '/pending'];

/** Strips a trailing slash so `/vehicles/` and `/vehicles` are one route. */
export function normalisePath(pathname: string): string {
  if (pathname.length > 1 && pathname.endsWith('/')) return pathname.replace(/\/+$/, '');
  return pathname || '/';
}

/** Whether `pathname` is the route at `routePath`, or sits under it. */
export function covers(routePath: string, pathname: string): boolean {
  return pathname === routePath || pathname.startsWith(`${routePath}/`);
}

/**
 * The screen a URL belongs to — the **longest** matching entry, so a nested
 * screen wins over its parent. `null` for a URL no screen claims.
 */
export function resolveScreenRoute(pathname: string): FleetScreen | null {
  const path = normalisePath(pathname);

  let best: FleetScreen | null = null;
  for (const screen of FLEET_SCREENS) {
    if (!covers(screen.path, path)) continue;
    if (!best || screen.path.length > best.path.length) best = screen;
  }
  return best;
}
