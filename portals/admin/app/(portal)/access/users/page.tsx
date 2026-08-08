import Link from 'next/link';

import { Button, Field, Input, Select, StatusPill } from '@mageride/ui';

import { read } from '@/api/client';
import { ProblemError, type ProblemDetails } from '@/api/problem';
import {
  CANONICAL_ROLES,
  RBAC_MATRIX_PATH,
  RBAC_ROLES_PATH,
  rbacSelection,
  userGrantsPath,
  type PermissionMatrix,
  type RoleCatalog,
  type UserRoleGrants,
} from '@/api/rbac';
import { ProblemPanel } from '@/components/ProblemPanel';
import {
  grantableRoles,
  permissionSetRows,
  roleLabel,
  userGrantsView,
  type RenderContext,
} from '@/components/rbac/model';
import { PermissionSetTable } from '@/components/rbac/PermissionSetTable';
import { RoleGrantForms } from '@/components/rbac/RoleGrantForms';
import { getLocale, getTranslator } from '@/i18n/server';
import { getSession } from '@/server/session';

/**
 * **SCR-AP-008 · `rbac`** — internal user and role management (Epic 21, AL-06).
 * **Super Admin only**, which is the nav item's own gate:
 * `FeatureAreas.RoleManagement · Write`, whose URD §2.3 row is
 * `➖ ➖ ➖ ➖ ✅ ➖ ➖ ➖ 👁`. Nothing on this page checks a role.
 *
 * ## It is a lookup, because there is no route that lists internal users
 *
 * The wireframe draws an "Internal users" table with an IP allow-list column, a
 * live-session column and a status column. iam-svc exposes `GET
 * /v1/admin/rbac/users/{userId}` and no list, no search and no provisioning route
 * — so that table cannot be populated, and it is not a case of a filter returning
 * nothing. The screen therefore asks for the id it can act on, which is what
 * `getUserRoleGrants` is described as serving: "what a Super Admin sees before
 * changing anything, and what an Auditor may read and not change."
 *
 * Four wireframe affordances have no route behind them and are absent rather than
 * dead, each recorded in the C108 handoff:
 *
 *  - the internal-user list;
 *  - **+ Provision user** (US-21.2's first half — no route creates an account);
 *  - **Revoke** *the account* and its live sessions (US-21.9) — role revocation is
 *    all the platform offers, and it is a different act;
 *  - the **IP allow-list** and **Active session** columns, which name state no
 *    contract on the platform exposes.
 *
 * ## The permission set is the matrix, read-only
 *
 * `?role=` picks whose column the card shows, defaulting to the looked-up user's
 * primary role. See `PermissionSetTable` for why the wireframe's toggles are cells.
 */

export const dynamic = 'force-dynamic';

export default async function RbacPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const selection = rbacSelection(params);

  const [t, locale] = await Promise.all([getTranslator(), getLocale()]);
  const context: RenderContext = { t, locale };

  // The session is read for nothing here: the nav item is the gate, and this
  // screen's own data all comes from iam-svc.
  await getSession();

  let catalog: RoleCatalog | null = null;
  let matrix: PermissionMatrix | null = null;
  let catalogProblem: ProblemDetails | null = null;

  try {
    [catalog, matrix] = await Promise.all([
      read<RoleCatalog>({ path: RBAC_ROLES_PATH }),
      read<PermissionMatrix>({ path: RBAC_MATRIX_PATH }),
    ]);
  } catch (error) {
    if (!(error instanceof ProblemError)) throw error;
    catalogProblem = error.problem;
  }

  let grants: UserRoleGrants | null = null;
  let grantsProblem: ProblemDetails | null = null;

  if (selection.userId) {
    try {
      grants = await read<UserRoleGrants>({ path: userGrantsPath(selection.userId) });
    } catch (error) {
      if (!(error instanceof ProblemError)) throw error;
      grantsProblem = error.problem;
    }
  }

  const view = grants ? userGrantsView(grants, context) : null;
  const shownRole = selection.role ?? view?.primaryRole.role ?? 'verification_officer';

  const decision = selection.granted
    ? t('admin.rbac.granted', { role: roleLabel(selection.granted, context) })
    : selection.revoked
      ? t('admin.rbac.revoked', { role: roleLabel(selection.revoked, context) })
      : null;

  return (
    <div className="flex flex-col gap-md">
      {decision ? (
        <p
          role="status"
          className="rounded-card border border-success/40 bg-success/10 p-sm text-body-sm text-on-surface"
        >
          {decision}
        </p>
      ) : null}

      <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
        <h2 className="text-subtitle font-semibold">{t('admin.rbac.lookupHeading')}</h2>
        <p className="text-caption text-on-surface-variant">{t('admin.rbac.noDirectory')}</p>

        <form method="get" action="/access/users" className="flex flex-wrap items-end gap-sm">
          <Field
            label={t('admin.rbac.userId')}
            hint={t('admin.rbac.userIdHint')}
            className="min-w-[320px] flex-1"
            {...(selection.typed && !selection.userId
              ? { error: t('admin.rbac.userIdInvalid') }
              : {})}
          >
            <Input
              name="userId"
              defaultValue={selection.typed}
              maxLength={40}
              autoCapitalize="none"
              spellCheck={false}
            />
          </Field>

          <Button type="submit" size="compact">
            {t('admin.rbac.lookup')}
          </Button>

          {selection.typed ? (
            <Link
              href="/access/users"
              className="inline-flex h-10 items-center rounded-sm px-md text-body-sm text-on-surface-variant underline underline-offset-2 hover:bg-surface-variant"
            >
              {t('admin.finance.filter.clear')}
            </Link>
          ) : null}
        </form>

        <p className="text-caption text-on-surface-variant">{t('admin.rbac.noProvisioning')}</p>
      </section>

      {grantsProblem ? <ProblemPanel problem={grantsProblem} /> : null}

      {view ? (
        <section className="flex flex-col gap-sm rounded-card border border-outline bg-background p-md shadow-card">
          <div className="flex flex-wrap items-center gap-sm">
            <h2 className="text-subtitle font-semibold">{t('admin.rbac.grantsHeading')}</h2>
            <StatusPill tone="neutral">{view.userId}</StatusPill>
          </div>

          <RoleGrantForms
            userId={view.userId}
            roles={view.roles}
            grantable={grantableRoles(catalog?.items ?? [], view.roles.map((role) => role.role)).map(
              (entry) => ({ role: entry.role, label: roleLabel(entry.role, context) }),
            )}
            labels={{
              grantHeading: t('admin.rbac.grantsHeading'),
              role: t('admin.rbac.role'),
              roleHint: t('admin.rbac.roleHint'),
              grant: t('admin.rbac.grant'),
              granting: t('admin.rbac.granting'),
              revoke: t('admin.rbac.revoke'),
              revoking: t('admin.rbac.revoking'),
              primary: t('admin.rbac.primary'),
              nothingToGrant: t('admin.rbac.nothingToGrant'),
              audit: t('admin.audit.notRecorded', { service: 'iam-svc' }),
            }}
          />

          <p className="text-caption text-on-surface-variant">{t('admin.rbac.noAccountRevoke')}</p>
        </section>
      ) : null}

      {catalogProblem ? <ProblemPanel problem={catalogProblem} /> : null}

      <form method="get" action="/access/users" className="flex flex-wrap items-end gap-sm">
        {selection.userId ? (
          <input type="hidden" name="userId" value={selection.userId} />
        ) : null}

        <Field label={t('admin.rbac.showRole')} className="w-[260px]">
          <Select name="role" defaultValue={shownRole}>
            {CANONICAL_ROLES.map((role) => (
              <option key={role} value={role}>
                {roleLabel(role, context)}
              </option>
            ))}
          </Select>
        </Field>

        <Button type="submit" size="compact" variant="secondary">
          {t('admin.rbac.showRoleApply')}
        </Button>
      </form>

      <PermissionSetTable
        rows={permissionSetRows(matrix?.areas ?? [], shownRole, context)}
        labels={{
          heading: t('admin.rbac.permissionSet', { role: roleLabel(shownRole, context) }),
          note: t('admin.rbac.readOnly'),
          caption: t('admin.rbac.permissionCaption'),
          area: t('admin.rbac.area'),
          cell: t('admin.rbac.cell'),
          capabilities: t('admin.rbac.capabilities'),
          empty: t('admin.rbac.permissionEmpty'),
        }}
      />

      {view ? (
        <PermissionSetTable
          rows={view.permissions}
          labels={{
            heading: t('admin.rbac.effectiveHeading'),
            note: t('admin.rbac.effectiveNote'),
            caption: t('admin.rbac.effectiveCaption'),
            area: t('admin.rbac.area'),
            cell: t('admin.rbac.cell'),
            capabilities: t('admin.rbac.capabilities'),
            empty: t('admin.rbac.permissionEmpty'),
          }}
        />
      ) : null}

      <p className="text-caption text-on-surface-variant">{t('admin.rbac.matrixNote')}</p>
    </div>
  );
}
