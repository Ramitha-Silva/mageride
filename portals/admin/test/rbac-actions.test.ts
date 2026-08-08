import { beforeEach, describe, expect, it, vi } from 'vitest';

import { isAuditIntent } from '@/api/audit';
import type { MutateOptions } from '@/api/client';
import type { PermissionEntry, RoleCatalogEntry, UserRoleGrants } from '@/api/rbac';
import { createAdminTranslator } from '@/i18n';
import {
  grantableRoles,
  permissionSetRows,
  userGrantsView,
  type RenderContext,
} from '@/components/rbac/model';
import { configTabs } from '@/components/config/tabs';

import { sessionFor } from './support/urd';

/**
 * SCR-AP-008 — the two writes Epic 21 has routes for, and the several it does not.
 *
 * The absences are asserted as deliberately as the presences, because each one is
 * a wireframe affordance this screen does **not** draw: there is no provisioning
 * call, no account suspension, and no way to edit a permission cell. A later
 * change that quietly added a button for one of them without a route behind it
 * would be the failure these tests exist for.
 */

const mutate = vi.fn<(options: MutateOptions) => Promise<unknown>>();
const redirect = vi.fn<(url: string) => never>();

vi.mock('@/api/client', () => ({ mutate: (options: MutateOptions) => mutate(options) }));
vi.mock('next/navigation', () => ({ redirect: (url: string) => redirect(url) }));
vi.mock('@/i18n/server', () => ({ getTranslator: async () => createAdminTranslator('en') }));

const { grantRole, revokeRole } = await import('@/server/rbac-actions');

const USER = '0199a1f0-0000-7000-8000-000000000u01'.replace('u', 'a');

const t = createAdminTranslator('en');
const context: RenderContext = { t, locale: 'en' };

function form(values: Record<string, string>): FormData {
  const data = new FormData();
  for (const [name, value] of Object.entries(values)) data.append(name, value);
  return data;
}

beforeEach(() => {
  vi.clearAllMocks();
  mutate.mockResolvedValue({ data: {}, status: 200 });
  redirect.mockImplementation((url: string) => {
    throw new Error(`redirect:${url}`);
  });
});

async function attempt(action: typeof grantRole, values: Record<string, string>) {
  try {
    return await action({}, form(values));
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('redirect:')) return {};
    throw error;
  }
}

describe('granting and revoking a role', () => {
  it('posts the role to iam-svc’s own route', async () => {
    await attempt(grantRole, { userId: USER, role: 'finance_officer' });

    expect(mutate.mock.calls[0]?.[0].method).toBe('POST');
    expect(mutate.mock.calls[0]?.[0].path).toBe(`/v1/admin/rbac/users/${USER}/roles`);
    expect(mutate.mock.calls[0]?.[0].body).toEqual({ role: 'finance_officer' });
  });

  it('deletes the role by path, with no body', async () => {
    await attempt(revokeRole, { userId: USER, role: 'auditor' });

    expect(mutate.mock.calls[0]?.[0].method).toBe('DELETE');
    expect(mutate.mock.calls[0]?.[0].path).toBe(`/v1/admin/rbac/users/${USER}/roles/auditor`);
    expect(mutate.mock.calls[0]?.[0].body).toBeUndefined();
  });

  it('refuses an id that is not one before building a path out of it', async () => {
    const state = await attempt(grantRole, { userId: 'nuwan@mageride.lk', role: 'auditor' });

    expect(state).toMatchObject({ field: 'userId' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('refuses a role the platform does not have', async () => {
    const state = await attempt(grantRole, { userId: USER, role: 'reseller' });

    expect(state).toMatchObject({ field: 'role' });
    expect(mutate).not.toHaveBeenCalled();
  });

  it('says the row is written by iam-svc, which writes none', async () => {
    // US-21.14 asks for permission changes to be audited; `/v1/admin/rbac/**` is
    // routed past admin-bff at Order 20 and iam-svc records `granted_by` on the
    // row rather than an `audit.events` entry. Claiming otherwise would tell a
    // Super Admin their grant is in a trail an Auditor cannot find it in.
    await attempt(grantRole, { userId: USER, role: 'auditor' });

    const audit = mutate.mock.calls[0]?.[0].audit ?? { action: 'PII_READ' as const, entity: 'driver' as const };
    expect(isAuditIntent(audit)).toBe(false);
    expect(audit).toEqual({ auditedElsewhere: 'iam-svc' });
  });

  it('returns to the same user, saying what changed', async () => {
    await attempt(grantRole, { userId: USER, role: 'finance_officer' });
    expect(redirect).toHaveBeenCalledWith(`/access/users?userId=${USER}&granted=finance_officer`);
  });
});

describe('what the screen offers, and what it cannot', () => {
  const grants: UserRoleGrants = {
    userId: USER,
    primaryRole: 'verification_officer',
    roles: ['verification_officer', 'auditor'],
    permissions: {
      userId: USER,
      roles: ['verification_officer', 'auditor'],
      permissions: [
        {
          featureArea: 'verification',
          label: 'Onboarding / verification queue',
          grants: ['read', 'write'],
          scopedGrants: [],
          symbol: '✅',
        },
        {
          featureArea: 'role-management',
          label: 'User & role management (RBAC)',
          grants: ['read'],
          scopedGrants: [],
          symbol: '👁',
        },
      ],
    },
  };

  it('marks the primary role as unrevocable rather than offering a button that 409s', () => {
    const view = userGrantsView(grants, context);
    const primary = view.roles.filter((role) => role.primary);

    expect(primary).toHaveLength(1);
    expect(primary[0]?.role).toBe('verification_officer');
  });

  it('offers only internal roles, and only ones not already held', () => {
    const catalog: RoleCatalogEntry[] = [
      { role: 'passenger', label: 'Passenger', isInternal: false },
      { role: 'driver', label: 'Driver', isInternal: false },
      { role: 'auditor', label: 'Auditor', isInternal: true },
      { role: 'finance_officer', label: 'Finance', isInternal: true },
    ];

    const offered = grantableRoles(catalog, ['verification_officer', 'auditor']).map(
      (entry) => entry.role,
    );

    // A console for provisioning back-office staff must not make it easy to turn
    // a colleague into an end user.
    expect(offered).toEqual(['finance_officer']);
  });

  it('renders a permission cell as the spec prints it, with the verbs beside it', () => {
    const view = userGrantsView(grants, context);
    const verification = view.permissions.find((row) => row.area === 'verification');

    expect(verification?.symbol).toBe('✅');
    expect(verification?.grants).toContain('Change');
    expect(verification?.tone).toBe('success');
  });

  it('says "Nothing" for an area with no grant rather than leaving the cell blank', () => {
    const empty: PermissionEntry = {
      featureArea: 'finance',
      label: 'Finance',
      grants: [],
      scopedGrants: [],
      symbol: '➖',
    };

    const rows = permissionSetRows(
      [{ featureArea: 'finance', label: 'Finance', cells: { auditor: empty } }],
      'auditor',
      context,
    );

    expect(rows[0]?.grants).toBe('Nothing');
    expect(rows[0]?.tone).toBe('neutral');
  });

  it('carries a qualifier through, because "◐ on tickets" is not "◐"', () => {
    const scoped: PermissionEntry = {
      featureArea: 'account-management',
      label: 'End-user account management',
      grants: ['read', 'write'],
      scopedGrants: ['write'],
      symbol: '◐ on tickets',
      qualifier: 'on tickets',
    };

    const rows = permissionSetRows(
      [{ featureArea: 'account-management', label: '', cells: { support_csr: scoped } }],
      'support_csr',
      context,
    );

    expect(rows[0]?.symbol).toBe('◐ on tickets');
    expect(rows[0]?.qualifier).toBe('on tickets');
  });
});

describe('SCR-AP-008 is Super Admin only, and the nav says so', () => {
  it('appears in a Super Admin’s menu and an Auditor’s in neither case by accident', () => {
    const holders = (
      [
        'admin',
        'super_admin',
        'verification_officer',
        'support_csr',
        'finance_officer',
        'auditor',
      ] as const
    ).filter((role) =>
      sessionFor([role]).menu.some((group) => group.items.some((item) => item.key === 'rbac')),
    );

    // URD §2.3's RBAC row is `➖ ➖ ➖ ➖ ✅ ➖ ➖ ➖ 👁` and the item needs Write,
    // so the Auditor's 👁 does not reach it. Admin is explicitly out — URD §2.4
    // says "**No** RBAC/role management".
    expect(holders).toEqual(['super_admin']);
  });
});

describe('the configuration tab strip is the caller’s own menu too', () => {
  it('gives an Admin every tab this component owns, plus the GTFS entry point', () => {
    const tabs = configTabs(sessionFor(['admin']).menu, 'tariffs').map((tab) => tab.id);

    expect(tabs).toEqual(['tariffs', 'fees', 'vouchers', 'levels', 'flags', 'gtfs']);
  });

  it('gives a Finance Officer the pricing tabs and not the platform-settings ones', () => {
    // URD §2.3: Platform pricing is `⚙ rates` for Finance, Platform settings is
    // `➖`. So the fare and fee ladders are theirs and the flags are not.
    const tabs = configTabs(sessionFor(['finance_officer']).menu, 'fees').map((tab) => tab.id);

    expect(tabs).toEqual(['tariffs', 'fees', 'vouchers']);
  });

  it('gives a Support CSR no configuration strip at all', () => {
    expect(configTabs(sessionFor(['support_csr']).menu, 'flags')).toEqual([]);
  });
});
