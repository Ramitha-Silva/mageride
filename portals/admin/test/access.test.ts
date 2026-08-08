import { describe, expect, it } from 'vitest';

import type { Role } from '@/api/types';
import { isReachable, landingPath, permittedItems, resolveScreen } from '@/server/access';
import { ADMIN_ROUTES } from '@/server/routes';

import { menuFor, sessionFor } from './support/urd';

/**
 * The Definition of Done, in two halves:
 *
 *   "a Support/CSR session sees only the nav entries URD §2.3 permits, and
 *    direct-URL access to a forbidden route 403s"
 *
 * Both are asserted against a menu **derived from the URD's own §2.3 table** and
 * admin-bff's own nav manifest (see `./support/urd.ts`). Nothing here is a
 * transcription of the expected answer, so the test fails if the spec changes,
 * if a nav item's gate changes, or if the portal's route table drifts from either.
 */

const CSR: readonly Role[] = ['support_csr'];
const VERIFICATION_OFFICER: readonly Role[] = ['verification_officer'];
const AUDITOR: readonly Role[] = ['auditor'];
const SUPER_ADMIN: readonly Role[] = ['super_admin'];

function itemKeys(roles: readonly Role[]): string[] {
  return permittedItems(menuFor(roles)).map(({ item }) => item.key);
}

describe('a Support/CSR session sees only what URD §2.3 permits', () => {
  const permitted = new Set(itemKeys(CSR));

  it('opens the screens the CSR column grants', () => {
    // Analytics 👁 · Verification 👁 · Support ✅ · Driver wallet 👁 ·
    // Fleet monitoring 👁 · Moderation ◐ · Refunds ◐ raise/recommend.
    expect([...permitted].sort()).toEqual(
      [
        'dashboard',
        'document-expiry',
        'drivers',
        'fraud-review',
        'passengers',
        'refunds',
        'reports',
        'support-tickets',
        'vehicles',
        'verification',
      ].sort(),
    );
  });

  it.each([
    // Finance: CSR is ➖. A CSR investigating a ticket has the passenger
    // directory and no business reading the platform's settlement position.
    ['reconciliation', '/finance/reconciliation'],
    ['transactions', '/finance/transactions'],
    // Driver wallet adjustments: ✅ for exactly Super Admin and Finance.
    ['wallet-adjustments', '/finance/adjustments'],
    // End-user account management: CSR is ◐ on tickets, and the data-rights
    // screen needs Write held *unscoped*. Fulfilling a PDPA erasure is not
    // "limited action on a ticket".
    ['pdpa', '/pdpa'],
    // Audit trail: CSR is ➖.
    ['audit-log', '/audit-log'],
    // Both Platform config rows: CSR is ➖.
    ['fare-tariffs', '/config/fares'],
    ['feature-flags', '/config/feature-flags'],
    ['gtfs', '/config/transit/gtfs'],
    // RBAC: ✅ for Super Admin alone.
    ['rbac', '/access/users'],
    // Announcements: CSR is ➖.
    ['announcements', '/announcements'],
  ])('refuses %s', (key, path) => {
    expect(permitted.has(key)).toBe(false);
    expect(isReachable(menuFor(CSR), path)).toBe(false);
  });

  it('refuses a forbidden screen reached by direct URL, including a deep link into it', () => {
    const menu = menuFor(CSR);

    expect(isReachable(menu, '/access/users')).toBe(false);
    expect(isReachable(menu, '/access/users/01JQ0000000000000000000000')).toBe(false);
    expect(isReachable(menu, '/config/transit/gtfs/versions')).toBe(false);
    // Trailing slashes are the same route, not a way round it.
    expect(isReachable(menu, '/access/users/')).toBe(false);
  });

  it('opens a deep link into a screen it does permit', () => {
    const menu = menuFor(CSR);
    expect(isReachable(menu, '/passengers')).toBe(true);
    expect(isReachable(menu, '/passengers/01JQ0000000000000000000000')).toBe(true);
    expect(resolveScreen(menu, '/passengers/01JQ0')?.item.key).toBe('passengers');
  });
});

describe('deny-by-default', () => {
  it('refuses a path no screen claims, for every role', () => {
    for (const roles of [CSR, VERIFICATION_OFFICER, AUDITOR, SUPER_ADMIN]) {
      expect(isReachable(menuFor(roles), '/not-a-screen')).toBe(false);
      expect(isReachable(menuFor(roles), '/finance')).toBe(false);
      expect(isReachable(menuFor(roles), '/config')).toBe(false);
    }
  });

  it('refuses everything when the menu is empty', () => {
    for (const route of ADMIN_ROUTES) {
      expect(isReachable([], route.path)).toBe(false);
    }
  });

  it('never lets a permitted parent screen open a forbidden nested one', () => {
    // `/verification` and `/verification/expiring` are two screens, and the
    // second is not reachable by being under the first. They share a gate today,
    // so the property is asserted structurally: every route whose path sits
    // under another route's path must resolve to its own key.
    for (const route of ADMIN_ROUTES) {
      const parent = ADMIN_ROUTES.find(
        (candidate) => candidate !== route && route.path.startsWith(`${candidate.path}/`),
      );
      if (!parent) continue;

      expect(isReachable(menuFor(SUPER_ADMIN), route.path)).toBe(true);
      expect(resolveScreen(menuFor(SUPER_ADMIN), route.path)?.item.key).toBe(route.key);
    }
  });

  it('lets the sign-in flow through without a session', () => {
    for (const path of ['/login', '/auth/callback', '/auth/google']) {
      expect(isReachable([], path)).toBe(true);
    }
  });
});

describe('landing', () => {
  it('sends a Verification Officer to the onboarding queue, not to a dashboard', () => {
    // URD §2.3 gives VER ➖ on "Analytics & reporting", so there is no dashboard
    // to land on — which is exactly what D2 §AP means by "Verification Officer →
    // onboarding queue only".
    const session = sessionFor(VERIFICATION_OFFICER);

    expect(itemKeys(VERIFICATION_OFFICER)).not.toContain('dashboard');
    expect(landingPath(session)).toBe('/verification');
  });

  it('sends everyone whose first screen is the dashboard to the dashboard', () => {
    for (const roles of [CSR, AUDITOR, SUPER_ADMIN, ['admin'] as Role[], ['finance_officer'] as Role[]]) {
      expect(landingPath(sessionFor(roles))).toBe('/dashboard');
    }
  });

  it('has nowhere to send a caller with no permitted screen', () => {
    expect(landingPath(sessionFor([]))).toBeNull();
  });
});

describe('the other internal roles', () => {
  it('gives the Verification Officer the onboarding queues and the two review queues', () => {
    // D2 §AP's shorthand is "Verification Officer → onboarding queue only", and
    // URD §2.3 — which is what the API enforces — is one cell wider than that:
    // the Moderation row gives VER `◐ at onboarding`, so the two *queues* open
    // and the platform-wide suspend button does not (admin-bff gates that with
    // `RequirePlatformWideFeature`). The matrix wins; recorded in the handoff.
    expect(itemKeys(VERIFICATION_OFFICER).sort()).toEqual([
      'document-expiry',
      'fraud-review',
      'reports',
      'verification',
    ]);

    // What "onboarding only" is actually right about: no dashboard, no
    // directory, no finance, no configuration, no audit log.
    for (const key of ['dashboard', 'passengers', 'drivers', 'vehicles', 'refunds', 'audit-log', 'rbac']) {
      expect(itemKeys(VERIFICATION_OFFICER)).not.toContain(key);
    }
  });

  it('gives the Auditor read access across the console and no RBAC screen', () => {
    const keys = itemKeys(AUDITOR);
    expect(keys).toContain('audit-log');
    expect(keys).toContain('dashboard');
    // URD §2.3's RBAC row is ✅ for Super Admin and 👁 for the Auditor; the nav
    // item needs Write, which "no write access anywhere" (URD §2.4) denies.
    expect(keys).not.toContain('rbac');
  });

  it('gives the RBAC screen to the Super Admin alone', () => {
    expect(itemKeys(SUPER_ADMIN)).toContain('rbac');
    for (const roles of [['admin'], ['support_csr'], ['finance_officer'], ['auditor'], ['verification_officer']] as Role[][]) {
      expect(itemKeys(roles)).not.toContain('rbac');
    }
  });

  it('makes a union of roles additive, never subtractive', () => {
    const csrOnly = new Set(itemKeys(CSR));
    const both = new Set(itemKeys(['support_csr', 'finance_officer']));

    for (const key of csrOnly) expect(both.has(key)).toBe(true);
    // Finance brings the settlement screens a CSR is refused (URD §2.1: the
    // effective set is the union).
    expect(both.has('reconciliation')).toBe(true);
  });
});
