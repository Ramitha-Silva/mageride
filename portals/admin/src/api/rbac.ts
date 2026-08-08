/**
 * SCR-AP-008's wire shapes — iam-svc's five `/v1/admin/rbac/**` operations, which
 * the gateway routes past admin-bff at Order 20.
 *
 * Transcribed from `backend/contracts/iam.yaml`, tag `rbac`.
 *
 * ## What Epic 21 asks for, and what the platform has a route for
 *
 * | Epic 21 | Route |
 * |---|---|
 * | US-21.2 assign one or more roles | `POST`/`DELETE …/users/{userId}/roles` |
 * | US-21.4 union, resolved on change | `GET …/users/{userId}` returns it |
 * | US-21.2 **provision** an internal account | **none** |
 * | US-21.3 custom permission sets | **none, deliberately** |
 * | US-21.9 suspend / reactivate / revoke an account + its sessions | **none** |
 * | list the internal users | **none** |
 *
 * The third and the last are gaps and are raised in the C108 handoff. The
 * **second is not a gap**: `getPermissionMatrix`'s own description says the matrix
 * is "read-only, and deliberately so — the matrix is a specification, not
 * configuration. A Super Admin who could edit it could grant themselves something
 * URD §2.3 forbids, which is the one thing the matrix exists to prevent." So the
 * wireframe's permission-set toggles are drawn as **read-only cells of URD §2.3**,
 * and the screen says which role's row it is showing. A toggle that silently did
 * nothing would be worse than the row it replaced.
 *
 * ## Which means the screen is a lookup, not a directory
 *
 * With no route that lists internal users, the wireframe's "Internal users" table
 * cannot be populated — not filtered down to nothing, *not answerable*. The screen
 * therefore takes a user id and shows that user's grants, which is exactly what
 * `GET …/users/{userId}` is for ("what a Super Admin sees before changing
 * anything, and what an Auditor may read and not change"). See the handoff.
 */

import type { Role } from './types';

/* ---------------------------------------------------------------------------
 * Paths
 * ------------------------------------------------------------------------ */

/** `GET /v1/admin/rbac/matrix` — the whole URD §2.3 matrix. Read-only by design. */
export const RBAC_MATRIX_PATH = '/v1/admin/rbac/matrix';

/** `GET /v1/admin/rbac/roles` — the nine canonical roles. */
export const RBAC_ROLES_PATH = '/v1/admin/rbac/roles';

/** `GET /v1/admin/rbac/users/{userId}` and the two writes under it. */
export const RBAC_USERS_PATH = '/v1/admin/rbac/users';

export function userGrantsPath(userId: string): string {
  return `${RBAC_USERS_PATH}/${userId}`;
}

export function grantRolePath(userId: string): string {
  return `${RBAC_USERS_PATH}/${userId}/roles`;
}

export function revokeRolePath(userId: string, role: Role): string {
  return `${RBAC_USERS_PATH}/${userId}/roles/${role}`;
}

/* ---------------------------------------------------------------------------
 * Wire shapes
 * ------------------------------------------------------------------------ */

/**
 * One capability from the URD §2.3 legend.
 *
 * Wider than `types.ts`'s `PermissionGrant` by one member: iam-svc's enum carries
 * `ownScope`, which admin-bff's session payload lifts onto a separate boolean.
 * Two servers, two spellings of one idea, and the portal transcribes each where it
 * is used rather than reconciling them into a third.
 */
export const RBAC_GRANTS = ['read', 'write', 'configure', 'raise', 'ownScope'] as const;

export type RbacGrant = (typeof RBAC_GRANTS)[number];

/** One URD §2.3 cell, as iam-svc renders it. */
export interface PermissionEntry {
  /** Stable key for a URD §2.3 row, e.g. `driver-wallet-adjustments`. */
  readonly featureArea: string;
  /** The row's URD §2.3 wording. Developer-facing; never rendered as copy. */
  readonly label: string;
  readonly grants: readonly RbacGrant[];
  /** The subset available **only** within the caller's own records or organisation. */
  readonly scopedGrants: readonly RbacGrant[];
  /** The cell verbatim, qualifier included — `✅`, `◐ on tickets`, `⚙ rates`. */
  readonly symbol: string;
  /** The scope note the cell carries, if any — `own`, `own org`, `financial`, `rates`. */
  readonly qualifier?: string;
}

export interface EffectivePermissions {
  readonly userId: string;
  readonly roles: readonly Role[];
  readonly fleetRole?: string;
  readonly fleetId?: string;
  /** One entry per URD §2.3 area. **Areas with no grant are present and empty.** */
  readonly permissions: readonly PermissionEntry[];
}

export interface MatrixArea {
  readonly featureArea: string;
  readonly label: string;
  /** Role → cell. **Every one of the nine is present — there is no default.** */
  readonly cells: Readonly<Record<string, PermissionEntry>>;
}

export interface PermissionMatrix {
  readonly roles: readonly Role[];
  readonly areas: readonly MatrixArea[];
}

export interface RoleCatalogEntry {
  readonly role: Role;
  readonly label: string;
  /** Roles 4–9, provisioned only by a Super Admin (AL-06). */
  readonly isInternal: boolean;
}

export interface RoleCatalog {
  readonly items: readonly RoleCatalogEntry[];
}

export interface UserRoleGrants {
  readonly userId: string;
  /**
   * `iam.users.role`. **Cannot be revoked as a grant** — the union in
   * `IUserRepository.RolesAsync` would keep handing it back, so iam-svc answers
   * `409`, and the screen says so on the row rather than offering the button.
   */
  readonly primaryRole: Role;
  /** The union that decides permissions — grants plus the primary role (AL-06). */
  readonly roles: readonly Role[];
  readonly fleetRole?: string;
  readonly fleetId?: string;
  readonly permissions: EffectivePermissions;
}

/* ---------------------------------------------------------------------------
 * Query state
 * ------------------------------------------------------------------------ */

const ADMIN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Whether a value is an id this portal will interpolate into an API path.
 *
 * The whole of SCR-AP-008 hangs off an id an operator types, because there is no
 * user list to click a row in — so this guard is doing more work here than
 * anywhere else on the surface. A mistyped id must fail as a mistyped id, before
 * the request, and not as a 404 from a URL this process built out of whatever was
 * in the box.
 */
export function isAdminId(value: string | undefined | null): value is string {
  return typeof value === 'string' && ADMIN_ID.test(value);
}

/** The nine canonical roles, in `_shared.yaml`'s order. */
export const CANONICAL_ROLES: readonly Role[] = [
  'passenger',
  'driver',
  'fleet_owner',
  'admin',
  'super_admin',
  'verification_officer',
  'support_csr',
  'finance_officer',
  'auditor',
];

export function isRole(value: string | undefined): value is Role {
  return value !== undefined && (CANONICAL_ROLES as readonly string[]).includes(value);
}

export interface RbacSelection {
  /** The user being looked at, when the box holds a well-formed id. */
  readonly userId?: string;
  /** What was typed, well-formed or not — so the box keeps it and can say what is wrong. */
  readonly typed: string;
  /** The role whose URD §2.3 row the permission-set card is showing. */
  readonly role?: Role;
  /** A grant or revocation that just succeeded, so the screen can confirm it. */
  readonly granted?: Role;
  readonly revoked?: Role;
}

function first(value: string | readonly string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : (value as string | undefined);
}

export function rbacSelection(
  params: Readonly<Record<string, string | readonly string[] | undefined>>,
): RbacSelection {
  const typed = (first(params.userId) ?? '').trim();
  const role = first(params.role);
  const granted = first(params.granted);
  const revoked = first(params.revoked);

  return {
    typed,
    ...(isAdminId(typed) ? { userId: typed } : {}),
    ...(isRole(role) ? { role } : {}),
    ...(isRole(granted) ? { granted } : {}),
    ...(isRole(revoked) ? { revoked } : {}),
  };
}

/** The screen, aimed at one user and optionally at one role's matrix row. */
export function rbacHref(selection: { userId?: string; role?: Role }): string {
  const query = new URLSearchParams();
  if (selection.userId) query.set('userId', selection.userId);
  if (selection.role) query.set('role', selection.role);

  const search = query.toString();
  return search ? `/access/users?${search}` : '/access/users';
}
