import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import type {
  FleetOrganisation,
  FleetPermission,
  FleetRole,
  FleetSession,
  FleetStatus,
  PermissionGrant,
} from '@/api/types';

/**
 * Builds a Fleet Portal session **from the specifications**, so the portal's
 * tests assert against URD §2.3 and URD §2.1's fleet sub-model rather than
 * against a fixture somebody typed out.
 *
 * Three files are read and nothing is transcribed:
 *
 *  - `specs/user-requirements-document.md` §2.3 — the 21 × 9 Feature Permission
 *    Matrix, cell for cell;
 *  - `backend/src/MageRide.Shared/Auth/PermissionModel.cs` — `FeatureAreas.All`,
 *    the row keys in the order the spec prints them, which is the order that file
 *    promises;
 *  - `backend/src/MageRide.Shared/Auth/PermissionEvaluator.cs` — mirrored below,
 *    which is the only part that is a second implementation.
 *
 * That mirroring is deliberate rather than accidental. The assertion "a Viewer
 * sees no mutating control" is worth nothing if the expectation is a copy of the
 * answer the portal computed; written this way, a change to the matrix, to the
 * row order, or to the narrowing rule lands in this build as a changed
 * expectation and the portal has to still agree with it.
 *
 * It reproduces **only** what a fleet caller needs: the `fleet_owner` column and
 * the sub-role narrowing. No fleet member holds an internal role — iam-svc sends
 * an account with one to the Admin Portal (`PortalSignInService.ResolveSurface`).
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');

const URD = join(REPO_ROOT, 'specs/user-requirements-document.md');
const FEATURE_AREAS = join(REPO_ROOT, 'backend/src/MageRide.Shared/Auth/PermissionModel.cs');

export const CLAIMS_SOURCE = join(REPO_ROOT, 'backend/src/MageRide.Shared/Auth/MageRideClaims.cs');
export const FLEET_ENDPOINTS_SOURCE = join(
  REPO_ROOT,
  'backend/src/Fleet.Api/Endpoints/FleetEndpoints.cs',
);
export const FLEET_OPS_ENDPOINTS_SOURCE = join(
  REPO_ROOT,
  'backend/src/Fleet.Api/Endpoints/FleetOpsEndpoints.cs',
);

/** The two contracts C112's screens are transcribed from. */
export const FLEET_CONTRACT = join(REPO_ROOT, 'backend/contracts/fleet.yaml');
export const SHARED_CONTRACT = join(REPO_ROOT, 'backend/contracts/_shared.yaml');

/**
 * A `enum: [a, b, c]` list out of an OpenAPI document, found by its first member.
 *
 * Crude on purpose. The alternative is a YAML parser as a dependency of the test
 * suite, to read three lists that are each written on one line and each found by
 * the value the portal claims comes first.
 */
export function contractEnum(source: string, firstMember: string): string[] {
  const match = new RegExp(`enum: \\[${firstMember}[^\\]]*\\]`).exec(source);
  if (!match) throw new Error(`No enum beginning "${firstMember}" in the contract.`);
  return match[0]
    .replace(/^enum: \[/, '')
    .replace(/\]$/, '')
    .split(',')
    .map((entry) => entry.trim());
}

/* ---------------------------------------------------------------------------
 * URD §2.3's legend, as `PermissionCell.Parse` reads it
 * ------------------------------------------------------------------------ */

const enum Grant {
  None = 0,
  Read = 1,
  Write = 2,
  Configure = 4,
  Raise = 8,
  OwnScope = 16,
}

const CAPABILITIES = Grant.Read | Grant.Write | Grant.Configure | Grant.Raise;

interface Cell {
  readonly grants: Grant;
  readonly qualifier: string | null;
}

function parseCell(symbol: string): Cell {
  // Variation selectors travel with emoji in markdown and are not part of the
  // glyph the legend defines.
  const trimmed = symbol.replaceAll('️', '').replaceAll('*', '').trim();
  const separator = trimmed.indexOf(' ');
  const glyph = separator < 0 ? trimmed : trimmed.slice(0, separator);
  const rest = separator < 0 ? '' : trimmed.slice(separator + 1).trim();
  const qualifier = rest === '' ? null : rest;

  let grants: Grant;
  switch (glyph) {
    case '✅':
      grants = Grant.Read | Grant.Write | Grant.Configure;
      break;
    case '⚙':
      grants = Grant.Read | Grant.Configure;
      break;
    case '👁':
      grants = Grant.Read;
      break;
    case '◐':
      grants = Grant.Read | Grant.Write | Grant.OwnScope;
      break;
    case '➖':
      grants = Grant.None;
      break;
    case 'raise':
    case 'report':
      grants = Grant.Raise;
      break;
    default:
      throw new Error(`"${glyph}" is not a URD §2.3 legend symbol (from "${symbol}").`);
  }

  return { grants: narrow(grants, qualifier), qualifier };
}

function narrow(grants: Grant, qualifier: string | null): Grant {
  if (qualifier === null || grants === Grant.None) return grants;

  const lower = qualifier.toLowerCase();
  if (lower === 'read') return (grants & ~(Grant.Write | Grant.Configure)) | Grant.Read;
  if (lower.includes('raise') || lower.includes('recommend')) {
    return (grants & ~Grant.Write) | Grant.Read | Grant.Raise;
  }
  if (lower === 'subset') return (grants & ~Grant.Write) | Grant.Read | Grant.Configure;
  return grants;
}

/* ---------------------------------------------------------------------------
 * Reading the two files
 * ------------------------------------------------------------------------ */

/** URD §2.3's column order — the `fleet_owner` column is the third. */
const FLEET_OWNER_COLUMN = 2;

/** `FeatureAreas.All` — the twenty-one row keys, in URD §2.3's own order. */
export function featureAreaKeys(): string[] {
  const source = readFileSync(FEATURE_AREAS, 'utf8');

  const declared = new Map<string, string>();
  const declaration = /public static readonly FeatureArea (\w+)\s*=\s*new\(\s*"([a-z-]+)"/g;
  for (const match of source.matchAll(declaration)) {
    declared.set(match[1]!, match[2]!);
  }

  const allBlock = /public static readonly IReadOnlyList<FeatureArea> All\s*=\s*\[([^\]]+)\]/s.exec(
    source,
  );
  if (!allBlock) throw new Error('FeatureAreas.All could not be read.');

  return allBlock[1]!
    .split(',')
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((name) => {
      const key = declared.get(name);
      if (!key) throw new Error(`FeatureAreas.${name} is listed in All but never declared.`);
      return key;
    });
}

/** URD §2.3's `fleet_owner` column, as `areaKey → cell`. */
export function fleetOwnerColumn(): Map<string, Cell> {
  const lines = readFileSync(URD, 'utf8').split('\n');

  const headerIndex = lines.findIndex((line) => line.startsWith('| Feature Area |'));
  if (headerIndex < 0) throw new Error('URD §2.3 header row not found.');

  const rows: string[][] = [];
  for (let index = headerIndex + 2; index < lines.length; index += 1) {
    const line = lines[index]!;
    if (!line.startsWith('|')) break;
    rows.push(
      line
        .split('|')
        .slice(1, -1)
        .map((cell) => cell.trim()),
    );
  }

  const keys = featureAreaKeys();
  if (rows.length !== keys.length) {
    throw new Error(
      `URD §2.3 has ${rows.length} rows and FeatureAreas.All has ${keys.length}. ` +
        'They are matched by position, which PermissionModel.cs promises.',
    );
  }

  const column = new Map<string, Cell>();
  rows.forEach((row, index) => {
    column.set(keys[index]!, parseCell(row[FLEET_OWNER_COLUMN + 1]!));
  });
  return column;
}

/* ---------------------------------------------------------------------------
 * The evaluation, mirroring PermissionEvaluator.NarrowToFleetSubRole
 * ------------------------------------------------------------------------ */

const VERBS: readonly (readonly [Grant, PermissionGrant])[] = [
  [Grant.Read, 'read'],
  [Grant.Write, 'write'],
  [Grant.Configure, 'configure'],
  [Grant.Raise, 'raise'],
];

/**
 * `GET /v1/me/permissions`'s `permissions`, for a caller whose only role is
 * `fleet_owner` and whose seat is `fleetRole`.
 *
 * "Owner = full org control + billing; Manager = onboarding, assignment,
 * scheduling, monitoring (no billing/owner changes); Viewer = read-only"
 * (URD §2.1), which `PermissionEvaluator` applies as: Viewer is restricted to
 * `Read | OwnScope` in every row, and Manager in the `fleet-billing` row alone.
 */
export function permissionsFor(fleetRole: FleetRole | null): FleetPermission[] {
  const permissions: FleetPermission[] = [];

  for (const [area, cell] of fleetOwnerColumn()) {
    const ceiling =
      fleetRole === 'viewer' || (fleetRole === 'manager' && area === 'fleet-billing')
        ? Grant.Read | Grant.OwnScope
        : null;

    const grants = ceiling === null ? cell.grants : cell.grants & ceiling;
    if (grants === Grant.None) continue;

    permissions.push({
      featureArea: area,
      label: area,
      symbol: cell.qualifier ?? '',
      grants: VERBS.filter(([flag]) => (grants & flag & CAPABILITIES) !== 0).map(([, verb]) => verb),
      ...(cell.qualifier ? { qualifier: cell.qualifier } : {}),
    });
  }

  return permissions;
}

export const ORG_ID = '01JQF0000000000000000000FL';

export function organisation(
  status: FleetStatus,
  overrides: Partial<FleetOrganisation> = {},
): FleetOrganisation {
  return {
    fleetId: ORG_ID,
    name: 'Lanka Transit (Pvt) Ltd',
    registrationNo: 'PV-118842',
    status,
    createdAt: '2026-07-01T04:30:00Z',
    ...overrides,
  };
}

/** A session for a member holding `fleetRole` in an organisation at `status`. */
export function sessionFor(fleetRole: FleetRole, status: FleetStatus = 'APPROVED'): FleetSession {
  return {
    userId: '01JQ0000000000000000000000',
    roles: ['fleet_owner'],
    fleetRole,
    fleetId: ORG_ID,
    permissions: permissionsFor(fleetRole),
    organisation: organisation(status),
  };
}

/**
 * A `fleet_owner` who has not registered an organisation yet — the state
 * `PermissionEvaluator` describes as "a fleet_owner with no membership row keeps
 * the plain column".
 */
export function sessionWithoutOrganisation(): FleetSession {
  return {
    userId: '01JQ0000000000000000000000',
    roles: ['fleet_owner'],
    fleetRole: null,
    fleetId: null,
    permissions: permissionsFor(null),
    organisation: null,
  };
}

/** An account that signed in and holds no fleet role at all. */
export function sessionWithoutFleetRole(): FleetSession {
  return {
    userId: '01JQ0000000000000000000000',
    roles: ['passenger'],
    fleetRole: null,
    fleetId: null,
    permissions: [],
    organisation: null,
  };
}
