/**
 * The iam-svc and fleet-svc wire shapes the shell depends on, transcribed from
 * `backend/contracts/iam.yaml` and `backend/contracts/fleet.yaml`.
 *
 * Only what the **shell** needs is here. The screen-group components (C112…C116)
 * own the shapes of their own vehicles, assignments, schedules and invoices; a
 * single portal-wide types file that grew every screen's payload would make each
 * of them edit a file they do not own.
 */

/** `_shared.yaml#/components/schemas/CursorPage`, with the items typed. */
export interface CursorPage<T> {
  readonly items: readonly T[];
  readonly cursor: string | null;
  readonly hasMore: boolean;
}

/** `_shared.yaml#/components/schemas/Role` — the nine canonical roles (AL-06). */
export type Role =
  | 'driver'
  | 'passenger'
  | 'fleet_owner'
  | 'admin'
  | 'super_admin'
  | 'verification_officer'
  | 'support_csr'
  | 'finance_officer'
  | 'auditor';

/**
 * `_shared.yaml#/components/schemas/FleetRole` — the org-scoped sub-model of the
 * `fleet_owner` role (AL-03, URD §2.1), most to least privileged.
 */
export type FleetRole = 'owner' | 'manager' | 'viewer';

/** `PermissionEntry.grants` — the URD §2.3 legend as verbs. */
export type PermissionGrant = 'read' | 'write' | 'configure' | 'raise';

/**
 * One URD §2.3 row as this caller holds it — `iam.yaml#/PermissionEntry`.
 *
 * The narrowing that matters here has already happened: `PermissionEvaluator`
 * applies URD §2.1's fleet sub-model to the `fleet_owner` column before it
 * answers, so a Viewer's rows carry `read` and no `write`, and a Manager's
 * `fleet-billing` row carries `read` and no `write`. The portal reads the result;
 * it does not reproduce the rule.
 */
export interface FleetPermission {
  /** Stable kebab key of the row, e.g. `fleet-operations`. */
  readonly featureArea: string;
  /** The row's URD §2.3 wording. Developer-facing; never rendered as copy. */
  readonly label: string;
  /** The cell as the spec prints it — `◐ own org`, `👁`, `➖`. */
  readonly symbol: string;
  readonly grants: readonly PermissionGrant[];
  /** The scope note the cell carries — `own org`, and for a narrowed cell also `viewer`. */
  readonly qualifier?: string;
}

/**
 * `GET /v1/me/permissions` — `iam.yaml#/EffectivePermissions`.
 *
 * **This is the Fleet Portal's session endpoint**, in the sense
 * `GET /v1/admin/session` is the Admin Portal's: it is the server's own
 * evaluation of who the caller is, which organisation they belong to and what
 * they may do in it, computed by the same `IPermissionEvaluator` every fleet-svc
 * route gates on. `fleetId` and `fleetRole` come from `iam.fleet_members` rather
 * than from the token, which carries only the most privileged of a person's
 * memberships (`FleetAccessFilter`).
 *
 * Both are optional on the wire: an account that holds `fleet_owner` and belongs
 * to no organisation yet is exactly the state a Fleet Owner is in between being
 * granted the role and registering their org (`PermissionEvaluator`).
 */
export interface EffectivePermissions {
  readonly userId: string;
  readonly roles: readonly Role[];
  readonly fleetRole?: FleetRole;
  readonly fleetId?: string;
  readonly permissions: readonly FleetPermission[];
}

/** `fleet.yaml#/components/schemas/FleetStatus` — the three states a row can hold. */
export type FleetStatus = 'PENDING' | 'APPROVED' | 'REJECTED';

/**
 * `GET /v1/fleets/{fleetId}` — `fleet.yaml#/components/schemas/Fleet`.
 *
 * Δ `fleet.yaml` also lists `SUSPENDED`; the `registry.fleets` CHECK does not
 * admit it and nothing in D3'/D5'/URD Epic 13 transitions to it, so C058 left it
 * out and this type does too. See the C058 handoff.
 */
export interface FleetOrganisation {
  readonly fleetId: string;
  readonly name: string;
  readonly registrationNo?: string;
  readonly contactPhone?: string;
  readonly contactEmail?: string;
  readonly address?: string;
  readonly status: FleetStatus;
  /** Set when a Verification Officer refused the organisation (US-13.A7). */
  readonly rejectionReason?: string;
  readonly createdAt: string;
}

/**
 * What the shell knows about the caller after one render's two reads.
 *
 * `organisation` is `null` for an account that holds `fleet_owner` and has not
 * registered one — the only signed-in state in which the portal has no org to
 * scope anything to, and the reason `fleetPath()` refuses rather than guessing.
 */
export interface FleetSession {
  readonly userId: string;
  readonly roles: readonly Role[];
  readonly fleetRole: FleetRole | null;
  readonly fleetId: string | null;
  readonly permissions: readonly FleetPermission[];
  readonly organisation: FleetOrganisation | null;
}

/** `iam.yaml#/components/schemas/UserProfile`, narrowed to what the chrome shows. */
export interface UserProfile {
  readonly userId: string;
  readonly email?: string;
  readonly firstName?: string;
  readonly photoUrl?: string;
  readonly role: Role;
  readonly roles?: readonly Role[];
  readonly fleetRole?: FleetRole;
  /** `si` | `ta` | `en` — the account's stored language (D-26). */
  readonly language?: string;
}

/** `_shared.yaml#/components/schemas/TokenPair` plus the profile all three arms return. */
export interface AuthSession {
  readonly accessToken: string;
  readonly refreshToken: string;
  /** Access-token lifetime in seconds (const 1800 — D-29). */
  readonly expiresIn: number;
  readonly user: UserProfile;
}

/** `POST /v1/auth/refresh` — the rotated pair, with no profile. */
export interface TokenPair {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly expiresIn: number;
}
