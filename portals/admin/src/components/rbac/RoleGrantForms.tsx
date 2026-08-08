'use client';

import { useActionState } from 'react';

import { Button, Field, Select, StatusPill } from '@mageride/ui';

import type { Role } from '@/api/types';
import { grantRole, revokeRole, type RoleGrantState } from '@/server/rbac-actions';

import type { GrantedRoleView } from './model';

/**
 * SCR-AP-008's two writes: grant a canonical role, revoke one (US-21.2, US-21.9).
 *
 * ## The primary role has no revoke button
 *
 * `iam.users.role` is part of the union that decides permissions and cannot be
 * removed as a *grant* — iam-svc answers `409` because `RolesAsync` would keep
 * handing it back. The row is drawn with a badge instead of a button, so the one
 * refusal an operator would otherwise hit by trying is a thing the screen already
 * told them.
 *
 * ## Neither is written to the audit trail, and the card says so
 *
 * `/v1/admin/rbac/**` is routed past admin-bff at the gateway, and iam-svc records
 * `granted_by` on the row rather than an `audit.events` entry. US-21.14 asks for
 * the opposite. Saying so here is what stops this console telling a Super Admin
 * that a grant is in a trail an Auditor will not find it in. See the C108 handoff.
 */

export interface RoleGrantLabels {
  readonly grantHeading: string;
  readonly role: string;
  readonly roleHint: string;
  readonly grant: string;
  readonly granting: string;
  readonly revoke: string;
  readonly revoking: string;
  readonly primary: string;
  readonly nothingToGrant: string;
  readonly audit: string;
}

const INITIAL: RoleGrantState = {};

export function RoleGrantForms({
  userId,
  roles,
  grantable,
  labels,
}: {
  userId: string;
  roles: readonly GrantedRoleView[];
  /** The internal roles this user does not already hold. */
  grantable: readonly { readonly role: Role; readonly label: string }[];
  labels: RoleGrantLabels;
}) {
  const [grantState, grantAction, granting] = useActionState(grantRole, INITIAL);
  const [revokeState, revokeAction, revoking] = useActionState(revokeRole, INITIAL);

  return (
    <div className="flex flex-col gap-sm">
      <ul className="flex flex-wrap items-center gap-sm">
        {roles.map((role) => (
          <li key={role.role} className="flex items-center gap-xs">
            <StatusPill tone={role.primary ? 'info' : 'neutral'}>{role.label}</StatusPill>

            {role.primary ? (
              <span className="text-caption text-on-surface-variant">{labels.primary}</span>
            ) : (
              <form action={revokeAction}>
                <input type="hidden" name="userId" value={userId} />
                <input type="hidden" name="role" value={role.role} />
                <Button
                  type="submit"
                  size="compact"
                  variant="danger"
                  disabled={revoking}
                  busy={revoking}
                  busyLabel={labels.revoking}
                >
                  {labels.revoke}
                </Button>
              </form>
            )}
          </li>
        ))}
      </ul>

      {revokeState.message ? (
        <p role="alert" className="text-body-sm text-error">
          {revokeState.message}
        </p>
      ) : null}

      <form action={grantAction} className="flex flex-wrap items-end gap-sm">
        <input type="hidden" name="userId" value={userId} />

        <Field
          label={labels.role}
          hint={labels.roleHint}
          className="w-[260px]"
          {...(grantState.message ? { error: grantState.message } : {})}
        >
          <Select name="role" disabled={grantable.length === 0}>
            {grantable.map((entry) => (
              <option key={entry.role} value={entry.role}>
                {entry.label}
              </option>
            ))}
          </Select>
        </Field>

        <Button
          type="submit"
          size="compact"
          disabled={granting || grantable.length === 0}
          busy={granting}
          busyLabel={labels.granting}
        >
          {labels.grant}
        </Button>

        {grantable.length === 0 ? (
          <span className="mb-sm text-caption text-on-surface-variant">
            {labels.nothingToGrant}
          </span>
        ) : null}
      </form>

      <p className="text-caption text-on-surface-variant">{labels.audit}</p>
    </div>
  );
}
