import { describe, expect, it } from 'vitest';

import { financeTabs } from '@/components/finance/tabs';
import { holdsGrant } from '@/server/access';

import { adminMenuManifest, Grant, permissionMatrix, sessionFor } from './support/urd';

/**
 * The Definition-of-Done items that are claims about **who may do what**, held
 * against URD §2.3 as the spec prints it rather than against a fixture.
 *
 * Two of them are C108's own:
 *
 *  - "a wallet reversal requires the Finance or Super Admin role";
 *  - the refund queue reaches a Support CSR and its raise form does not.
 *
 * Neither is enforced by code in this portal, and that is the point: the first is
 * the nav item's own gate and the second is a capability admin-bff evaluated. What
 * these tests assert is that the console offers exactly what the matrix permits,
 * so a change to either would land here as a changed expectation.
 */

describe('DoD — a wallet reversal is Finance or Super Admin', () => {
  const manifest = adminMenuManifest().flatMap((group) => group.items);
  const item = manifest.find((entry) => entry.key === 'wallet-adjustments');

  it('is a nav item gated on Driver-wallet-adjustments · Write', () => {
    expect(item, 'AdminMenu.cs has no wallet-adjustments item').toBeDefined();
    expect(item?.area).toBe('driver-wallet-adjustments');
    expect(item?.needed).toBe(Grant.Write);
  });

  it('resolves through URD §2.3 to exactly Super Admin and Finance', () => {
    // The fence is not a role list written into a route or into this portal. It
    // is what the matrix already says — `➖ ➖ ➖ 👁 ✅ ➖ ➖ ✅ 👁` — and this is
    // that row, read from the URD and evaluated the way the platform evaluates it.
    const row = permissionMatrix().get('driver-wallet-adjustments');
    expect(row, 'URD §2.3 has no driver-wallet-adjustments row').toBeDefined();

    const writers = [...(row?.entries() ?? [])]
      .filter(([, cell]) => (cell.grants & Grant.Write) !== 0)
      .map(([role]) => role)
      .sort();

    expect(writers).toEqual(['finance_officer', 'super_admin']);
  });

  it('puts the screen in those two menus and in nobody else’s', () => {
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
      sessionFor([role]).menu.some((group) =>
        group.items.some((entry) => entry.key === 'wallet-adjustments'),
      ),
    );

    expect([...holders].sort()).toEqual(['finance_officer', 'super_admin']);
  });
});

describe('DoD — the refund queue reaches a CSR and its button does not', () => {
  it('gives the CSR the screen', () => {
    const csr = sessionFor(['support_csr']);
    const items = csr.menu.flatMap((group) => group.items).map((item) => item.key);

    expect(items).toContain('refunds');
  });

  it('withholds Refunds · Write from them and grants it to Finance', () => {
    // `◐ raise/recommend` against `✅ approve/execute` — the one URD §2.3 row
    // whose two halves are a screen and a button.
    expect(holdsGrant(sessionFor(['support_csr']), 'refunds', 'write')).toBe(false);
    expect(holdsGrant(sessionFor(['support_csr']), 'refunds', 'read')).toBe(true);
    expect(holdsGrant(sessionFor(['finance_officer']), 'refunds', 'write')).toBe(true);
  });

  it('gives an Auditor the read and no write anywhere this console gates a control', () => {
    const auditor = sessionFor(['auditor']);

    expect(holdsGrant(auditor, 'refunds', 'write')).toBe(false);
    expect(holdsGrant(auditor, 'driver-wallet-adjustments', 'write')).toBe(false);
    expect(holdsGrant(auditor, 'audit-trail', 'read')).toBe(true);
  });
});

describe('holdsGrant reads the coarse boolean, and that is safe here', () => {
  it('no internal role holds a scope-limited write in a row this console gates a control on', () => {
    // `AdminPermission.ownScope` is `ScopedGrants != None` and does not say *which*
    // capability is scoped, so `holdsGrant` cannot make admin-bff's precise
    // `RequiresOwnScope(needed)` check and takes the coarse reading instead. That
    // is only sound while no internal role's `write` in these rows is own-scope.
    // If URD §2.3 ever grants one, this fails and the coarse reading has to go.
    const GATED_ROWS = ['refunds', 'driver-wallet-adjustments', 'finance', 'audit-trail'];
    const INTERNAL = [
      'admin',
      'super_admin',
      'verification_officer',
      'support_csr',
      'finance_officer',
      'auditor',
    ] as const;

    const matrix = permissionMatrix();

    for (const area of GATED_ROWS) {
      const row = matrix.get(area);
      expect(row, `URD §2.3 has no ${area} row`).toBeDefined();

      for (const role of INTERNAL) {
        const cell = row?.get(role);
        if (!cell) continue;

        const scopedWrite = (cell.grants & Grant.Write) !== 0 && (cell.grants & Grant.OwnScope) !== 0;
        expect(scopedWrite, `${role} holds an own-scope write on ${area}`).toBe(false);
      }
    }
  });
});

describe('the finance tab strip is the caller’s own menu', () => {
  it('gives a Finance Officer every tab the wireframe draws', () => {
    const tabs = financeTabs(sessionFor(['finance_officer']).menu, 'settlement').map((tab) => tab.id);

    expect(tabs).toEqual(['settlement', 'ledger', 'refunds', 'reversals', 'payouts', 'transfers']);
  });

  it('gives a Support CSR the refunds tab and nothing else', () => {
    // A strip built from anything but the menu would offer them four screens
    // `proxy.ts` answers 403 on.
    const tabs = financeTabs(sessionFor(['support_csr']).menu, 'refunds').map((tab) => tab.id);

    expect(tabs).toEqual(['refunds']);
  });

  it('gives a Verification Officer no finance strip at all', () => {
    expect(financeTabs(sessionFor(['verification_officer']).menu, 'settlement')).toEqual([]);
  });

  it('takes each href from the path the server sent, not from the local route table', () => {
    const [settlement] = financeTabs(sessionFor(['finance_officer']).menu, 'settlement');
    expect(settlement?.href).toBe('/finance/reconciliation');

    const payouts = financeTabs(sessionFor(['finance_officer']).menu, 'settlement').find(
      (tab) => tab.id === 'payouts',
    );
    expect(payouts?.href).toBe('/finance/reconciliation?view=payouts');
  });

  it('marks exactly one tab as the current page', () => {
    const current = financeTabs(sessionFor(['super_admin']).menu, 'ledger').filter(
      (tab) => tab.current,
    );

    expect(current).toHaveLength(1);
    expect(current[0]?.id).toBe('ledger');
  });
});
