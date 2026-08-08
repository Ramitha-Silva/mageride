import { readFileSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import { FLEET_NAV, FLEET_SCREENS, PUBLIC_PATHS, UNSCREENED_PATHS } from '@/server/routes';

import {
  CLAIMS_SOURCE,
  FLEET_ENDPOINTS_SOURCE,
  FLEET_OPS_ENDPOINTS_SOURCE,
  featureAreaKeys,
} from './support/fleet';

/**
 * The manifest against the service it describes.
 *
 * `src/server/routes.ts` transcribes three declarations off fleet-svc's endpoints
 * — the URD §2.3 row, the minimum sub-role and the approval gate — and a
 * transcription that nothing checks is a transcription that drifts. These tests
 * parse the C# and fail the build when it moves.
 */

const FLEET_ENDPOINTS = readFileSync(FLEET_ENDPOINTS_SOURCE, 'utf8');
const FLEET_OPS_ENDPOINTS = readFileSync(FLEET_OPS_ENDPOINTS_SOURCE, 'utf8');

describe('the manifest is internally consistent', () => {
  it('has a unique key and a unique path for every screen', () => {
    const keys = FLEET_SCREENS.map((screen) => screen.key);
    const paths = FLEET_SCREENS.map((screen) => screen.path);

    expect(new Set(keys).size).toBe(keys.length);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it('names only URD §2.3 rows that exist', () => {
    const areas = new Set(featureAreaKeys());
    for (const screen of FLEET_SCREENS) {
      expect(areas.has(screen.area), `${screen.key} names "${screen.area}"`).toBe(true);
    }
  });

  it('gives every screen an absolute path and a wireframe id', () => {
    for (const screen of FLEET_SCREENS) {
      expect(screen.path.startsWith('/'), screen.key).toBe(true);
      expect(screen.screen, screen.key).toMatch(/^SCR-FP-\d{3}a?$/);
    }
  });

  it('covers SCR-FP-002…012 — every wireframe screen except the sign-in card', () => {
    // SCR-FP-001 is the sign-in screen and belongs to no nav entry: it is what a
    // signed-out person sees, so it cannot be a member of a menu that only exists
    // once there is a session.
    const covered = new Set(FLEET_SCREENS.map((screen) => screen.screen));
    for (const id of [
      'SCR-FP-002',
      'SCR-FP-002a',
      'SCR-FP-003',
      'SCR-FP-004',
      'SCR-FP-005',
      'SCR-FP-006',
      'SCR-FP-007',
      'SCR-FP-008',
      'SCR-FP-009',
      'SCR-FP-010',
      'SCR-FP-011',
      'SCR-FP-012',
    ]) {
      expect(covered.has(id), `${id} has no route`).toBe(true);
    }
  });

  it('keeps the sign-in flow public and the two refusal pages unscreened', () => {
    expect(PUBLIC_PATHS).toEqual([
      '/login',
      '/auth/google',
      '/auth/apple',
      '/auth/callback/google',
      '/auth/callback/apple',
    ]);
    expect(UNSCREENED_PATHS).toEqual(['/', '/denied', '/pending']);

    // A screen path that was also public would be a hole rather than an exemption.
    for (const screen of FLEET_SCREENS) {
      expect(PUBLIC_PATHS).not.toContain(screen.path);
      expect(UNSCREENED_PATHS).not.toContain(screen.path);
    }
  });
});

describe('the sub-role ladder is the platform’s own', () => {
  it('matches FleetRoles in MageRideClaims.cs', () => {
    const source = readFileSync(CLAIMS_SOURCE, 'utf8');
    const block = /public static class FleetRoles\s*\{([\s\S]*?)\n\}/.exec(source);
    expect(block, 'FleetRoles could not be read').not.toBeNull();

    const ranks = new Map<string, number>();
    for (const match of block![1]!.matchAll(/(Owner|Manager|Viewer) => (\d+)/g)) {
      ranks.set(match[1]!.toLowerCase(), Number(match[2]));
    }

    expect([...ranks.entries()].sort((a, b) => b[1] - a[1]).map(([role]) => role)).toEqual([
      'owner',
      'manager',
      'viewer',
    ]);

    // Every sub-role the manifest names has to be one the platform defines.
    for (const screen of FLEET_SCREENS) {
      expect(ranks.has(screen.minimumFleetRole), screen.key).toBe(true);
    }
  });
});

describe('the approval gate is exactly fleet-svc’s', () => {
  /**
   * The two groups `FleetEndpoints` builds with `RequireApprovedFleet()`, named by
   * the constants every other file hangs routes off. Everything under either
   * prefix is gated; everything else carries the gate per route or not at all.
   */
  it('is declared on the two route groups the portal blocks', () => {
    expect(FLEET_ENDPOINTS).toMatch(
      /FleetVehiclesGroup\s*=\s*"\/v1\/fleets\/\{fleetId\}\/vehicles"/,
    );
    expect(FLEET_ENDPOINTS).toMatch(
      /FleetAssignmentsGroup\s*=\s*"\/v1\/fleets\/\{fleetId\}\/assignments"/,
    );

    // Both groups, in both files, carry the gate on the group builder itself.
    const gatedGroups = [...FLEET_ENDPOINTS.matchAll(/MapGroup\((FleetVehiclesGroup|FleetAssignmentsGroup)\)[\s\S]{0,400}?RequireApprovedFleet\(\)/g)];
    expect(gatedGroups.length).toBeGreaterThanOrEqual(2);

    const opsGated = [...FLEET_OPS_ENDPOINTS.matchAll(/MapGroup\(FleetEndpoints\.(FleetVehiclesGroup|FleetAssignmentsGroup)\)[\s\S]{0,400}?RequireApprovedFleet\(\)/g)];
    expect(opsGated.length).toBeGreaterThanOrEqual(2);
  });

  it('blocks the screens whose routes are inside those groups and no others', () => {
    const blocked = FLEET_SCREENS.filter((screen) => screen.requiresApprovedOrg).map(
      (screen) => screen.key,
    );

    // Vehicles and assignments themselves, plus the Mode B subscription proxies —
    // which `FleetOpsEndpoints.MapSubscriptionProxies` deliberately builds on the
    // vehicle group so they inherit the same gate.
    expect(blocked.sort()).toEqual(['drivers', 'payments', 'subscriptions', 'vehicles']);
  });

  it('leaves the ops reads ungated, as FleetOpsEndpoints does', () => {
    // The map, analytics, alerts, schedules and geofence *reads* hang off a group
    // with no approval gate; the three writes carry it individually. A portal that
    // blocked those screens would refuse a read the platform allows.
    const opsGroup = /var ops = endpoints\.MapGroup\(FleetOpsGroup\)([\s\S]*?);/.exec(
      FLEET_OPS_ENDPOINTS,
    );
    expect(opsGroup, 'the ops group could not be read').not.toBeNull();
    expect(opsGroup![1]).not.toContain('RequireApprovedFleet');

    for (const key of ['dashboard', 'map', 'analytics', 'scheduling', 'trackers']) {
      const screen = FLEET_SCREENS.find((candidate) => candidate.key === key)!;
      expect(screen.requiresApprovedOrg, key).toBe(false);
    }
  });
});

describe('the Owner-only routes the manifest claims really are Owner-only', () => {
  const ownerOnly = [
    'getPayoutProfile',
    'upsertPayoutProfile',
    'uploadPayoutProfileDocument',
    'addFleetMember',
  ];

  it('declares RequireFleetSubRole(FleetRoles.Owner) on each', () => {
    for (const operation of ownerOnly) {
      const declaration = new RegExp(
        `WithName\\("${operation}"\\)[\\s\\S]{0,200}?RequireFleetSubRole\\(FleetRoles\\.Owner\\)`,
      );
      expect(FLEET_ENDPOINTS, operation).toMatch(declaration);
    }
  });

  it('is what the payout and team entries are gated to', () => {
    expect(FLEET_SCREENS.find((screen) => screen.key === 'payout')?.minimumFleetRole).toBe('owner');
    // The team *roster* is Viewer — the invite control is what needs the Owner
    // seat, and `canManageTeam` is where that lives.
    expect(FLEET_SCREENS.find((screen) => screen.key === 'team')?.minimumFleetRole).toBe('viewer');
    expect(FLEET_ENDPOINTS).toMatch(
      /WithName\("listFleetMembers"\)[\s\S]{0,200}?RequireFleetSubRole\(FleetRoles\.Viewer\)/,
    );
  });

  it('matches the sub-roles the subscription proxies declare', () => {
    expect(FLEET_OPS_ENDPOINTS).toMatch(
      /WithName\("listFleetVehicleRequests"\)[\s\S]{0,200}?RequireFleetSubRole\(FleetRoles\.Manager\)/,
    );
    expect(FLEET_OPS_ENDPOINTS).toMatch(
      /WithName\("listFleetSubscriberPayments"\)[\s\S]{0,200}?RequireFleetSubRole\(FleetRoles\.Owner\)/,
    );

    expect(FLEET_SCREENS.find((screen) => screen.key === 'subscriptions')?.minimumFleetRole).toBe(
      'manager',
    );
    expect(FLEET_SCREENS.find((screen) => screen.key === 'payments')?.minimumFleetRole).toBe(
      'owner',
    );
  });
});

describe('the nav is the wireframe’s', () => {
  it('draws the four groups web_fleet.html draws, in its order', () => {
    expect(FLEET_NAV.map((group) => group.key)).toEqual([
      'setup',
      'operate',
      'manage',
      'subscribers',
    ]);
  });

  it('uses the wireframe’s own paths', () => {
    const paths = Object.fromEntries(FLEET_SCREENS.map((screen) => [screen.key, screen.path]));
    expect(paths).toMatchObject({
      organisation: '/org/setup',
      payout: '/org/payout',
      dashboard: '/dashboard',
      vehicles: '/vehicles',
      drivers: '/drivers',
      trackers: '/trackers',
      map: '/map',
      scheduling: '/scheduling',
      analytics: '/analytics',
      billing: '/billing',
      subscriptions: '/subscriptions',
      payments: '/payments',
    });
  });
});
