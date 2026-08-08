import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { AdminMenuGroup, AdminPermission, AdminSession, PermissionGrant, Role } from '@/api/types';

/**
 * Builds a caller's `GET /v1/admin/session` menu **from the specifications**, so
 * the portal's tests assert against URD §2.3 rather than against a fixture
 * somebody typed out.
 *
 * Three files are read and nothing is transcribed:
 *
 *  - `specs/user-requirements-document.md` §2.3 — the 21 × 9 Feature Permission
 *    Matrix, cell for cell;
 *  - `backend/src/MageRide.Shared/Auth/PermissionModel.cs` — `FeatureAreas.All`,
 *    the row keys in the order the spec prints them, which is the order that
 *    file promises;
 *  - `backend/src/AdminBff/Navigation/AdminMenu.cs` — the nav manifest and the
 *    (area, capability, platform-wide) pair each entry is gated on.
 *
 * The evaluation itself mirrors `PermissionCell.Parse` and `PermissionEvaluator`
 * (`MageRide.Shared.Auth`). That is a second implementation of a rule, which is
 * usually the thing to avoid — but here it is the point: the assertion "a
 * Support/CSR sees only the nav entries URD §2.3 permits" is worth nothing if the
 * expectation is a copy of the answer. Written this way, a change to the matrix,
 * to the row order, or to any nav item's gate lands in this build as a changed
 * expectation, and the portal's route table has to still agree with it.
 *
 * It reproduces **only** what a single-role internal caller needs: no fleet
 * sub-role narrowing (no internal role holds `fleet_owner`) and no symbol
 * rendering (nothing here reads `symbol`).
 */

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');

const URD = join(REPO_ROOT, 'specs/user-requirements-document.md');
const FEATURE_AREAS = join(REPO_ROOT, 'backend/src/MageRide.Shared/Auth/PermissionModel.cs');
export const ADMIN_MENU_SOURCE = join(
  REPO_ROOT,
  'backend/src/AdminBff/Navigation/AdminMenu.cs',
);

/* ---------------------------------------------------------------------------
 * URD §2.3's legend, as `PermissionCell.Parse` reads it
 * ------------------------------------------------------------------------ */

export const enum Grant {
  None = 0,
  Read = 1,
  Write = 2,
  Configure = 4,
  Raise = 8,
  OwnScope = 16,
}

const CAPABILITIES = Grant.Read | Grant.Write | Grant.Configure | Grant.Raise;

/** `PermissionGrant.<Name>` as `AdminMenu.cs` spells it. */
const GRANT_BY_NAME: Readonly<Record<string, Grant>> = {
  Read: Grant.Read,
  Write: Grant.Write,
  Configure: Grant.Configure,
  Raise: Grant.Raise,
};

interface Cell {
  readonly grants: Grant;
  readonly qualifier: string | null;
}

export function parseCell(symbol: string): Cell {
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
 * Reading the three files
 * ------------------------------------------------------------------------ */

/** URD §2.3's column order, as its own header row prints it. */
const COLUMNS: readonly Role[] = [
  'driver',
  'passenger',
  'fleet_owner',
  'admin',
  'super_admin',
  'verification_officer',
  'support_csr',
  'finance_officer',
  'auditor',
];

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

/** URD §2.3, as `areaKey → role → cell`. */
export function permissionMatrix(): Map<string, Map<Role, Cell>> {
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
        'They are matched by position, which FeatureAreas.cs promises ("in the order the spec prints them").',
    );
  }

  const matrix = new Map<string, Map<Role, Cell>>();
  rows.forEach((row, index) => {
    const cells = new Map<Role, Cell>();
    COLUMNS.forEach((role, column) => {
      cells.set(role, parseCell(row[column + 1]!));
    });
    matrix.set(keys[index]!, cells);
  });

  return matrix;
}

export interface MenuItemSpec {
  readonly key: string;
  readonly labelKey: string;
  readonly path: string;
  readonly area: string;
  readonly needed: Grant;
  readonly ownedBy: string;
  readonly platformWide: boolean;
}

export interface MenuGroupSpec {
  readonly key: string;
  readonly labelKey: string;
  readonly items: MenuItemSpec[];
}

/** `AdminMenu.All`, unfiltered. */
export function adminMenuManifest(): MenuGroupSpec[] {
  const source = readFileSync(ADMIN_MENU_SOURCE, 'utf8');
  const areaKeys = featureAreaByIdentifier();

  const groups: MenuGroupSpec[] = [];
  const groupPattern = /new\("([a-z-]+)",\s*"(nav\.group\.[A-Za-z]+)",/g;
  const itemPattern =
    /new\("([a-z-]+)",\s*"(nav\.[A-Za-z]+)",\s*"(\/[^"]*)",\s*FeatureAreas\.(\w+),\s*PermissionGrant\.(\w+),\s*(Self|"[a-z-]+")(,\s*PlatformWide:\s*(true|false))?\)/g;

  // Group boundaries first, then every item that falls between one boundary and
  // the next. The manifest is one array literal, so position is the structure.
  const boundaries = [...source.matchAll(groupPattern)];
  boundaries.forEach((group, index) => {
    const start = group.index + group[0].length;
    const end = boundaries[index + 1]?.index ?? source.length;
    const body = source.slice(start, end);

    const items: MenuItemSpec[] = [];
    for (const match of body.matchAll(itemPattern)) {
      const identifier = match[4]!;
      const area = areaKeys.get(identifier);
      if (!area) throw new Error(`AdminMenu names FeatureAreas.${identifier}, which does not exist.`);

      const needed = GRANT_BY_NAME[match[5]!];
      if (needed === undefined) {
        throw new Error(`AdminMenu names PermissionGrant.${match[5]}, which is not a capability.`);
      }

      items.push({
        key: match[1]!,
        labelKey: match[2]!,
        path: match[3]!,
        area,
        needed,
        ownedBy: match[6] === 'Self' ? 'admin-bff' : match[6]!.replaceAll('"', ''),
        platformWide: match[8] === 'true',
      });
    }

    groups.push({ key: group[1]!, labelKey: group[2]!, items });
  });

  if (groups.length === 0) throw new Error('AdminMenu.All could not be read.');
  return groups;
}

function featureAreaByIdentifier(): Map<string, string> {
  const source = readFileSync(FEATURE_AREAS, 'utf8');
  const declared = new Map<string, string>();
  const declaration = /public static readonly FeatureArea (\w+)\s*=\s*new\(\s*"([a-z-]+)"/g;
  for (const match of source.matchAll(declaration)) declared.set(match[1]!, match[2]!);
  return declared;
}

/* ---------------------------------------------------------------------------
 * The evaluation, mirroring PermissionEvaluator
 * ------------------------------------------------------------------------ */

interface Effective {
  readonly grants: Grant;
  /** Capabilities no role grants platform-wide — `EffectivePermission.ScopedGrants`. */
  readonly scopedOnly: Grant;
}

export function evaluate(roles: readonly Role[]): Map<string, Effective> {
  const matrix = permissionMatrix();
  const result = new Map<string, Effective>();

  for (const [area, cells] of matrix) {
    let unscoped = Grant.None;
    let scoped = Grant.None;

    for (const role of roles) {
      const cell = cells.get(role);
      if (!cell || cell.grants === Grant.None) continue;

      const capabilities = cell.grants & CAPABILITIES;
      if (cell.grants & Grant.OwnScope) scoped |= capabilities;
      else unscoped |= capabilities;
    }

    const scopedOnly = scoped & ~unscoped;
    const grants = unscoped | scoped | (scopedOnly === Grant.None ? Grant.None : Grant.OwnScope);
    result.set(area, { grants, scopedOnly });
  }

  return result;
}

/** `AdminMenu.For(...)` — the manifest as one caller sees it. */
export function menuFor(roles: readonly Role[]): AdminMenuGroup[] {
  const effective = evaluate(roles);

  return adminMenuManifest()
    .map((group) => ({
      key: group.key,
      labelKey: group.labelKey,
      items: group.items
        .filter((item) => {
          const permission = effective.get(item.area) ?? { grants: Grant.None, scopedOnly: Grant.None };
          const satisfies = item.needed !== Grant.None && (permission.grants & item.needed) === item.needed;
          const requiresOwnScope = (permission.scopedOnly & item.needed) !== Grant.None;
          return satisfies && (!item.platformWide || !requiresOwnScope);
        })
        .map((item) => ({
          key: item.key,
          labelKey: item.labelKey,
          path: item.path,
          ownedBy: item.ownedBy,
        })),
    }))
    // A group left with no items is dropped with them (AdminMenu.For).
    .filter((group) => group.items.length > 0);
}

/**
 * `AdminSession.permissions` — the caller's own row of URD §2.3, as
 * `SessionEndpoints` projects it (Δ C108).
 *
 * The same evaluation {@link menuFor} runs, rendered the way the wire renders it:
 * areas with no grant are dropped (`Where(Grants != None)`), the capability flags
 * become verbs, and `ownScope` is `ScopedGrants != None` — *some* capability here
 * is limited to the caller's own records, which is the coarse boolean the C#
 * response collapses `ScopedGrants` into.
 *
 * It exists because C108 gates a **control** rather than a screen: the refund
 * queue is one nav item with two audiences, and `holdsGrant` reads this field to
 * decide whether the raise form is drawn. A fixture of `[]` would have made that
 * test assert nothing.
 */
export function permissionsFor(roles: readonly Role[]): AdminPermission[] {
  const matrix = permissionMatrix();
  const effective = evaluate(roles);

  const VERBS: readonly (readonly [Grant, PermissionGrant])[] = [
    [Grant.Read, 'read'],
    [Grant.Write, 'write'],
    [Grant.Configure, 'configure'],
    [Grant.Raise, 'raise'],
  ];

  return [...effective.entries()]
    .filter(([, permission]) => permission.grants !== Grant.None)
    .map(([area, permission]) => {
      const cells = matrix.get(area);
      // The symbol is per (area, role) on the spec and per caller on the wire; a
      // single-role caller is what these fixtures build, so the first held cell is
      // the caller's. Nothing in the portal renders it.
      const cell = roles.map((role) => cells?.get(role)).find((held) => held && held.grants !== Grant.None);

      return {
        featureArea: area,
        label: area,
        symbol: cell?.qualifier ?? '',
        grants: VERBS.filter(([flag]) => (permission.grants & flag) !== 0).map(([, verb]) => verb),
        ...(cell?.qualifier ? { qualifier: cell.qualifier } : {}),
        ownScope: permission.scopedOnly !== Grant.None,
      };
    });
}

/** A `GET /v1/admin/session` payload for a caller holding `roles`. */
export function sessionFor(roles: readonly Role[]): AdminSession {
  return {
    userId: '01JQ0000000000000000000000',
    roles,
    permissions: permissionsFor(roles),
    menu: menuFor(roles),
    mfaRequired: false,
  };
}
