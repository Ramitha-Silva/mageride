import { describe, expect, it } from 'vitest';

import {
  canManageTeam,
  canMutate,
  dispositionFor,
  holdsGrant,
  landingPath,
  permittedNav,
  permittedScreens,
  refusalFor,
  satisfiesFleetRole,
} from '@/server/access';
import { FLEET_SCREENS, resolveScreenRoute } from '@/server/routes';

import {
  sessionFor,
  sessionWithoutFleetRole,
  sessionWithoutOrganisation,
} from './support/fleet';

/**
 * The component's Definition of Done, as assertions — against sessions built from
 * URD §2.3 and URD §2.1's fleet sub-model rather than from a hand-written fixture
 * (`./support/fleet.ts`).
 */

describe('a Viewer session renders no mutating control anywhere', () => {
  const viewer = sessionFor('viewer');

  it('holds write in no feature area at all', () => {
    // The strongest form of the DoD item: not "the buttons are hidden" but "there
    // is no row in which this session could be offered one". `PermissionEvaluator`
    // restricts the whole fleet_owner column to Read | OwnScope for a Viewer, and
    // this asserts the consequence rather than the rule.
    const writable = viewer.permissions.filter((permission) => permission.grants.includes('write'));
    expect(writable).toEqual([]);
  });

  it('refuses canMutate for every area the portal gates a control on', () => {
    for (const area of ['fleet-operations', 'fleet-billing', 'tracker-binding']) {
      expect(canMutate(viewer, area), area).toBe(false);
    }
    expect(canManageTeam(viewer)).toBe(false);
  });

  it('still reads the screens a Viewer is meant to read', () => {
    const keys = permittedScreens(viewer).map((screen) => screen.key);
    expect(keys).toContain('dashboard');
    expect(keys).toContain('map');
    expect(keys).toContain('analytics');
    expect(keys).toContain('vehicles');
  });

  it('is not offered the two Owner-only money screens', () => {
    const keys = permittedScreens(viewer).map((screen) => screen.key);
    expect(keys).not.toContain('payout');
    expect(keys).not.toContain('billing');
    expect(keys).not.toContain('payments');
  });
});

describe('a Manager loses billing and nothing else (URD §2.1)', () => {
  const manager = sessionFor('manager');

  it('may write to operations and trackers', () => {
    expect(canMutate(manager, 'fleet-operations')).toBe(true);
    expect(canMutate(manager, 'tracker-binding')).toBe(true);
  });

  it('may not write to billing, and does not see the two Owner-only screens', () => {
    expect(canMutate(manager, 'fleet-billing')).toBe(false);

    const keys = permittedScreens(manager).map((screen) => screen.key);
    expect(keys).not.toContain('payout');
    expect(keys).not.toContain('billing');
  });

  it('may not invite a team member — POST …/members is Owner-only', () => {
    expect(canManageTeam(manager)).toBe(false);
    expect(canManageTeam(sessionFor('owner'))).toBe(true);
  });

  it('still reads the billing row, which is why the service being stricter matters', () => {
    // URD §2.1 leaves a Manager `read` on fleet-billing. fleet-billing.yaml
    // requires the `owner` sub-role on every route "reads included", so the
    // portal gates the screen on `write` — see the note on the billing entry in
    // `src/server/routes.ts`. If the matrix ever loses this read the deviation
    // stops being one, and this assertion is what says so.
    expect(holdsGrant(manager, 'fleet-billing', 'read')).toBe(true);
  });
});

describe('an unapproved organisation cannot reach vehicle or assignment screens', () => {
  const pendingOwner = sessionFor('owner', 'PENDING');

  it('drops exactly the two screens fleet-svc gates on approval', () => {
    const approved = permittedScreens(sessionFor('owner')).map((screen) => screen.key);
    const pending = permittedScreens(pendingOwner).map((screen) => screen.key);

    expect(approved.filter((key) => !pending.includes(key)).sort()).toEqual([
      'drivers',
      'payments',
      'subscriptions',
      'vehicles',
    ]);
  });

  it('sends those URLs to the pending state rather than to a refusal', () => {
    expect(dispositionFor(pendingOwner, '/vehicles')).toBe('pending');
    expect(dispositionFor(pendingOwner, '/vehicles/onboard')).toBe('pending');
    expect(dispositionFor(pendingOwner, '/drivers')).toBe('pending');
  });

  it('leaves the setup screens open, because the payout profile is read before approval', () => {
    // AL-49: the bank documents are part of what the Verification Officer reads
    // *before* approving, so the payout screen cannot be behind the gate it feeds.
    expect(dispositionFor(pendingOwner, '/org/setup')).toBe('render');
    expect(dispositionFor(pendingOwner, '/org/payout')).toBe('render');
    expect(dispositionFor(pendingOwner, '/org/team')).toBe('render');
  });

  it('leaves the reads fleet-svc does not gate open too', () => {
    // Refusing something the platform allows is this portal inventing a refusal.
    // `GET …/schedules`, `GET …/map`, `GET …/analytics` and fleet-health's
    // rollup carry no approval gate.
    expect(dispositionFor(pendingOwner, '/dashboard')).toBe('render');
    expect(dispositionFor(pendingOwner, '/map')).toBe('render');
    expect(dispositionFor(pendingOwner, '/scheduling')).toBe('render');
    expect(dispositionFor(pendingOwner, '/trackers')).toBe('render');
  });

  it('lands a pending owner on the organisation screen, not on an empty dashboard', () => {
    expect(landingPath(pendingOwner)).toBe('/org/setup');
    expect(landingPath(sessionFor('owner'))).toBe('/dashboard');
  });

  it('treats a rejected organisation the same way as a pending one', () => {
    const rejected = sessionFor('manager', 'REJECTED');
    expect(dispositionFor(rejected, '/vehicles')).toBe('pending');
    expect(landingPath(rejected)).toBe('/org/setup');
  });
});

describe('an account with no organisation', () => {
  const owner = sessionWithoutOrganisation();

  it('reaches the one screen that creates one, and nothing else', () => {
    expect(permittedScreens(owner).map((screen) => screen.key)).toEqual(['organisation']);
    expect(landingPath(owner)).toBe('/org/setup');
  });

  it('is refused every org-scoped screen', () => {
    expect(dispositionFor(owner, '/dashboard')).toBe('denied');
    expect(dispositionFor(owner, '/vehicles')).toBe('denied');
    expect(dispositionFor(owner, '/billing')).toBe('denied');
  });

  it('can mutate nothing, because there is no organisation to mutate', () => {
    expect(canMutate(owner, 'fleet-operations')).toBe(false);
  });
});

describe('an account with no fleet role', () => {
  const stranger = sessionWithoutFleetRole();

  it('opens nothing, which is what the shell says out loud', () => {
    expect(permittedScreens(stranger)).toEqual([]);
    expect(permittedNav(stranger)).toEqual([]);
    expect(landingPath(stranger)).toBeNull();
  });
});

describe('the shape of the decision', () => {
  it('ranks the sub-roles as FleetRoles.Satisfies does', () => {
    expect(satisfiesFleetRole('owner', 'viewer')).toBe(true);
    expect(satisfiesFleetRole('manager', 'viewer')).toBe(true);
    expect(satisfiesFleetRole('manager', 'owner')).toBe(false);
    expect(satisfiesFleetRole('viewer', 'manager')).toBe(false);
    expect(satisfiesFleetRole(null, 'viewer')).toBe(false);
  });

  it('drops a nav group once its last item goes', () => {
    const viewer = sessionFor('viewer', 'PENDING');
    const groups = permittedNav(viewer).map((group) => group.key);

    // Subscriptions is Manager+ and Payments is Owner-only, so a pending Viewer
    // has no member of that group and must not be shown its heading.
    expect(groups).not.toContain('subscribers');
    for (const group of permittedNav(viewer)) {
      expect(group.items.length).toBeGreaterThan(0);
    }
  });

  it('resolves a nested URL to its own screen, longest prefix first', () => {
    expect(resolveScreenRoute('/org/payout')?.key).toBe('payout');
    expect(resolveScreenRoute('/vehicles/onboard')?.key).toBe('vehicles');
    expect(resolveScreenRoute('/vehicles/')?.key).toBe('vehicles');
    expect(resolveScreenRoute('/nowhere')).toBeNull();
  });

  it('answers not-found for a URL no screen claims, even for an Owner', () => {
    expect(dispositionFor(sessionFor('owner'), '/nowhere')).toBe('not-found');
  });

  it('refuses on the seat before it mentions the approval gate', () => {
    // A Viewer who may never open the subscriptions screen should be told that
    // rather than told to wait for an approval that would not help them.
    const viewer = sessionFor('viewer', 'PENDING');
    const subscriptions = FLEET_SCREENS.find((screen) => screen.key === 'subscriptions')!;
    expect(refusalFor(viewer, subscriptions)).toBe('denied');
  });
});
