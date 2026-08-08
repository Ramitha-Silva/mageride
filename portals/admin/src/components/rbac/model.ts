import type { StatusTone } from '@mageride/ui';

import type { MatrixArea, PermissionEntry, RoleCatalogEntry, UserRoleGrants } from '@/api/rbac';
import type { Role } from '@/api/types';
import type { AdminMessageKey, AdminTranslator, Locale } from '@/i18n';

/**
 * SCR-AP-008's view model.
 *
 * ## The permission rows are read, never edited
 *
 * `getPermissionMatrix` is explicit: "read-only, and deliberately so — the matrix
 * is a specification, not configuration. A Super Admin who could edit it could
 * grant themselves something URD §2.3 forbids, which is the one thing the matrix
 * exists to prevent." So the wireframe's permission-set toggles become **cells**,
 * rendered from the symbol the server sent (`✅`, `◐ on tickets`, `⚙ rates`) with
 * the capabilities spelled out beside it. A toggle that silently did nothing would
 * be worse than the row it replaced.
 *
 * `PermissionEntry.label` is the row's URD §2.3 wording and is **developer-facing**
 * — `types.ts` says so — so it is not what the operator reads. The screen renders
 * the symbol, which is the spec's own notation and identical in all three
 * languages, and translates the capability verbs.
 */

export interface RenderContext {
  readonly t: AdminTranslator;
  readonly locale: Locale;
}

const ROLE_LABEL = {
  admin: 'admin.role.admin',
  super_admin: 'admin.role.super_admin',
  verification_officer: 'admin.role.verification_officer',
  support_csr: 'admin.role.support_csr',
  finance_officer: 'admin.role.finance_officer',
  auditor: 'admin.role.auditor',
  driver: 'admin.role.driver',
  passenger: 'admin.role.passenger',
  fleet_owner: 'admin.role.fleet_owner',
} as const satisfies Record<Role, AdminMessageKey>;

export function roleLabel(role: Role, { t }: RenderContext): string {
  return t(ROLE_LABEL[role]);
}

const GRANT_LABEL = {
  read: 'admin.rbac.grant.read',
  write: 'admin.rbac.grant.write',
  configure: 'admin.rbac.grant.configure',
  raise: 'admin.rbac.grant.raise',
  ownScope: 'admin.rbac.grant.ownScope',
} as const satisfies Record<string, AdminMessageKey>;

function grantWords(entry: PermissionEntry, { t }: RenderContext): string {
  const words = entry.grants
    .filter((grant) => grant in GRANT_LABEL)
    .map((grant) => t(GRANT_LABEL[grant as keyof typeof GRANT_LABEL]));

  return words.length > 0 ? words.join(' · ') : t('admin.rbac.grant.none');
}

export interface GrantedRoleView {
  readonly role: Role;
  readonly label: string;
  /**
   * `iam.users.role`. **It cannot be revoked as a grant** — the union in
   * `IUserRepository.RolesAsync` would keep handing it back, so iam-svc answers
   * `409`. The row says so instead of offering a button that cannot work.
   */
  readonly primary: boolean;
}

export interface PermissionRowView {
  readonly key: string;
  /** URD §2.3's own notation, identical in all three languages. */
  readonly symbol: string;
  readonly grants: string;
  readonly qualifier: string | null;
  readonly tone: StatusTone;
  /** The row's key, e.g. `driver-wallet-adjustments`. Not the developer-facing label. */
  readonly area: string;
}

function toneFor(entry: PermissionEntry): StatusTone {
  if (entry.grants.length === 0) return 'neutral';
  if (entry.grants.includes('write') || entry.grants.includes('configure')) return 'success';
  return 'info';
}

export interface UserGrantsView {
  readonly userId: string;
  readonly primaryRole: GrantedRoleView;
  /** Every role in the union that decides permissions (AL-06). */
  readonly roles: readonly GrantedRoleView[];
  /** The caller's effective row of URD §2.3, one entry per area. */
  readonly permissions: readonly PermissionRowView[];
}

export function userGrantsView(
  grants: UserRoleGrants,
  context: RenderContext,
): UserGrantsView {
  return {
    userId: grants.userId,
    primaryRole: {
      role: grants.primaryRole,
      label: roleLabel(grants.primaryRole, context),
      primary: true,
    },
    roles: grants.roles.map((role) => ({
      role,
      label: roleLabel(role, context),
      primary: role === grants.primaryRole,
    })),
    permissions: grants.permissions.permissions.map((entry) => ({
      key: entry.featureArea,
      area: entry.featureArea,
      symbol: entry.symbol,
      grants: grantWords(entry, context),
      qualifier: entry.qualifier?.trim() ? entry.qualifier.trim() : null,
      tone: toneFor(entry),
    })),
  };
}

/**
 * One role's whole column of URD §2.3 — the wireframe's "Permission set —
 * Verification Officer" card, read-only.
 *
 * Built from `GET /v1/admin/rbac/matrix`, which carries every one of the nine
 * roles for every area ("there is no default"), so a role with nothing in a row
 * still has a cell and the card has no gaps.
 */
export function permissionSetRows(
  areas: readonly MatrixArea[],
  role: Role,
  context: RenderContext,
): PermissionRowView[] {
  return areas.flatMap((area) => {
    const cell = area.cells[role];
    if (!cell) return [];

    return [
      {
        key: `${area.featureArea}:${role}`,
        area: area.featureArea,
        symbol: cell.symbol,
        grants: grantWords(cell, context),
        qualifier: cell.qualifier?.trim() ? cell.qualifier.trim() : null,
        tone: toneFor(cell),
      },
    ];
  });
}

/**
 * The roles a Super Admin may grant: **the internal ones** (4–9).
 *
 * `RoleCatalogEntry.isInternal` is iam-svc's own flag for exactly this. Offering
 * `passenger` or `driver` in a console that exists to provision back-office staff
 * would be offering to turn a colleague into an end user, which is not what
 * US-21.2 is about and not a thing this screen should make easy.
 */
export function grantableRoles(
  catalog: readonly RoleCatalogEntry[],
  held: readonly Role[],
): RoleCatalogEntry[] {
  const already = new Set(held);
  return catalog.filter((entry) => entry.isInternal && !already.has(entry.role));
}
