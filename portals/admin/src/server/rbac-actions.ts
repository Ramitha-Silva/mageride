'use server';

import { redirect } from 'next/navigation';

import { mutate } from '@/api/client';
import { ProblemError } from '@/api/problem';
import { grantRolePath, isAdminId, isRole, revokeRolePath } from '@/api/rbac';
import type { Role } from '@/api/types';
import { getTranslator } from '@/i18n/server';

/**
 * SCR-AP-008's two decisions: granting a canonical role and revoking one
 * (US-21.2, Epic 21).
 *
 * ## Neither is audited, and that is a platform gap rather than a design choice
 *
 * `gateway-routes.json` matches `/v1/admin/rbac/**` at Order 20 and sends it to
 * iam-svc, which writes `iam.user_roles.granted_by` and nothing to `audit.events`;
 * admin-bff maps no route onto the prefix, so its interceptor never sees the call.
 * US-21.14 requires the opposite in as many words — "permission changes, role
 * assignments and internal-account lifecycle events are **themselves audited** and
 * visible to Auditors and Super Admins" — and SCR-AP-009's own wireframe draws a
 * `ROLE_GRANT Finance` row that nothing currently writes.
 *
 * So both calls declare `auditedElsewhere: 'iam-svc'`, the screen says so beside
 * the buttons, and the C108 handoff raises it as a micro-change-set. Telling an
 * operator their grant is in the trail when an Auditor will not find it there is
 * the one thing this console must not do.
 *
 * ## Neither checks who the caller is
 *
 * "Super Admin only" is URD §2.3's RoleManagement row (`➖ ➖ ➖ ➖ ✅ ➖ ➖ ➖ 👁`),
 * enforced by iam-svc's `RequireFeature(RoleManagement, Write)` and surfaced by the
 * nav item being absent for everybody else. A role check written here would be a
 * third copy of the matrix and the one nobody's test parses the spec to verify.
 */

export interface RoleGrantState {
  readonly message?: string;
  readonly field?: 'userId' | 'role';
}

function text(formData: FormData, name: string): string {
  const value = formData.get(name);
  return typeof value === 'string' ? value.trim() : '';
}

/**
 * Grant a canonical role (US-21.2).
 *
 * Idempotent — `iam.user_roles`' primary key settles a repeat and the granting
 * Super Admin is recorded in `granted_by` — so a double click is a no-op on the
 * far side and needs no stable key from here.
 */
export async function grantRole(
  _state: RoleGrantState,
  formData: FormData,
): Promise<RoleGrantState> {
  const t = await getTranslator();

  const userId = text(formData, 'userId');
  const role = text(formData, 'role');

  if (!isAdminId(userId)) return { message: t('admin.rbac.userIdInvalid'), field: 'userId' };
  if (!isRole(role)) return { message: t('admin.rbac.roleRequired'), field: 'role' };

  try {
    await mutate<unknown, { role: Role }>({
      method: 'POST',
      path: grantRolePath(userId),
      body: { role },
      audit: { auditedElsewhere: 'iam-svc' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  redirect(`/access/users?userId=${userId}&granted=${role}`);
}

/**
 * Revoke a canonical role (US-21.9's role half).
 *
 * **Two refusals, both `409` from iam-svc, and neither is re-implemented here.**
 * The account's *primary* role cannot be revoked as a grant, because the union in
 * `IUserRepository.RolesAsync` would keep handing it back; and a Super Admin cannot
 * revoke their own `super_admin`, because AL-06 makes them the only principal who
 * can grant it back. The screen does not draw a revoke control against the primary
 * role — that is a fact `GET …/users/{userId}` puts on the page — but the other is
 * about *who is asking*, which this process does not know and must not guess.
 */
export async function revokeRole(
  _state: RoleGrantState,
  formData: FormData,
): Promise<RoleGrantState> {
  const t = await getTranslator();

  const userId = text(formData, 'userId');
  const role = text(formData, 'role');

  if (!isAdminId(userId)) return { message: t('admin.rbac.userIdInvalid'), field: 'userId' };
  if (!isRole(role)) return { message: t('admin.rbac.roleRequired'), field: 'role' };

  try {
    await mutate({
      method: 'DELETE',
      path: revokeRolePath(userId, role),
      audit: { auditedElsewhere: 'iam-svc' },
    });
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    return { message: t(error.messageKey) };
  }

  redirect(`/access/users?userId=${userId}&revoked=${role}`);
}
