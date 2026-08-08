/**
 * The admin-bff and iam-svc wire shapes the shell depends on, transcribed from
 * `backend/contracts/admin-bff.yaml` and `backend/contracts/iam.yaml`.
 *
 * Only what the **shell** needs is here. The screen-group components (C105…C110)
 * own the shapes of their own queues, directories and reports; a single portal-wide
 * types file that grew every screen's payload would make each of them edit a file
 * they do not own.
 */

/**
 * `_shared.yaml#/components/schemas/CursorPage`, with the items typed.
 *
 * **Δ C107 — hoisted here from `./verification`.** It is not a screen's shape: it
 * is the envelope every paged admin-bff route answers in, and the third screen
 * group to need it was the point at which one of them owning it would have made
 * the other two import a queue module they have nothing to do with. `verification`
 * re-exports it, so C106's callers are untouched.
 */
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

/** The six roles that may sign in here at all (AL-02). */
export const INTERNAL_ROLES: readonly Role[] = [
  'admin',
  'super_admin',
  'verification_officer',
  'support_csr',
  'finance_officer',
  'auditor',
] as const;

/** `AdminPermission.grants` — the URD §2.3 legend as verbs. */
export type PermissionGrant = 'read' | 'write' | 'configure' | 'raise';

/** One URD §2.3 row as this caller holds it. */
export interface AdminPermission {
  /** Stable kebab key of the row, e.g. `platform-settings`. */
  readonly featureArea: string;
  /** The row's URD §2.3 wording. Developer-facing; never rendered as copy. */
  readonly label: string;
  /** The cell as the spec prints it — `✅`, `◐ own org`, `⚙ rates`. */
  readonly symbol: string;
  readonly grants: readonly PermissionGrant[];
  /** The scope note the cell carries, when anything is scope-limited. */
  readonly qualifier?: string;
  /**
   * At least one capability here is bounded to the caller's own records.
   * A fence, not an answer — the owning service is what bounds it.
   */
  readonly ownScope: boolean;
}

/**
 * One nav entry. `labelKey` is a **resource key, never a label** — D-26 makes every
 * user-facing string trilingual and the portal owns the Si/Ta/En bundles, so a
 * server that shipped English here would be the one string on the platform that
 * could not be translated.
 */
export interface AdminMenuItem {
  readonly key: string;
  readonly labelKey: string;
  /** The Admin Portal route. The gateway decides which process answers its API. */
  readonly path: string;
  /** Which service answers this item's API — six of them are not admin-bff's. */
  readonly ownedBy: string;
}

export interface AdminMenuGroup {
  readonly key: string;
  readonly labelKey: string;
  readonly items: readonly AdminMenuItem[];
}

/**
 * `GET /v1/admin/session` — what the portal fetches the moment sign-in completes
 * (URD §2.2). `permissions` is the caller's row of URD §2.3 as the server evaluated
 * it; `menu` is the **same evaluation** projected onto the nav, which is what makes
 * "the UI is rendered from the same permission model the API enforces" true rather
 * than aspirational.
 */
export interface AdminSession {
  readonly userId: string;
  readonly roles: readonly Role[];
  readonly permissions: readonly AdminPermission[];
  readonly menu: readonly AdminMenuGroup[];
  /**
   * **Always false (AL-37).** Present precisely because D3' §0 and D7' §4.2 still
   * carry the pre-AL-37 wording, and a portal built from those would sit waiting
   * for a challenge that is never coming.
   */
  readonly mfaRequired: false;
}

/** `iam.yaml#/components/schemas/UserProfile`, narrowed to what the chrome shows. */
export interface UserProfile {
  readonly userId: string;
  readonly email?: string;
  readonly firstName?: string;
  readonly photoUrl?: string;
  readonly role: Role;
  readonly roles?: readonly Role[];
  /** `si` | `ta` | `en` — the staff member's stored language (D-26). */
  readonly language?: string;
}

/** `_shared.yaml#/components/schemas/TokenPair` plus the profile both login arms return. */
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
