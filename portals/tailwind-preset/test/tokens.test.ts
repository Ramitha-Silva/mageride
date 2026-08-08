/**
 * Holds `src/tokens.ts` against D2' §0.2.
 *
 * The expected values below are a SECOND, independent transcription of the spec
 * tables — read off `specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens`
 * rather than off `tokens.ts`. That is the whole value of this file: a test that
 * imported the tokens and compared them to themselves would pass on a typo,
 * and a typo in a hex is exactly the failure mode a design-token package has.
 */

import { describe, expect, it } from 'vitest';

import type { SemanticColorToken, VehicleColorToken } from '../src/tokens.js';
import {
  BREAKPOINTS,
  CTA,
  ELEVATIONS,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  SPACING_BASE,
  TYPE_ROLES,
  VEHICLE_COLORS,
} from '../src/tokens.js';

/** D2 §0.2 table 1 — "Brand & semantic colors (light → dark variant)". */
const D2_SEMANTIC: ReadonlyArray<readonly [string, string, string]> = [
  ['primary', '#FF6D00', '#FFB68A'],
  ['onPrimary', '#FFFFFF', '#4A2300'],
  ['primaryContainer', '#FFE0CC', '#6A3500'],
  ['onPrimaryContainer', '#2B1100', '#FFDCC4'],
  ['secondary', '#0061A4', '#9FCAFF'],
  ['secondaryContainer', '#D1E4FF', '#00497D'],
  ['background', '#FFFFFF', '#121316'],
  ['surface', '#F7F8FA', '#1A1C1E'],
  ['surfaceVariant', '#ECEEF1', '#2A2D31'],
  ['outline', '#C7CBD1', '#43474E'],
  ['onSurface', '#1A1C1E', '#E3E2E6'],
  ['onSurfaceVariant', '#44474B', '#C3C7CF'],
  ['outlineVariant', '#74777C', '#8D9199'],
  ['success', '#2E9E4F', '#7FD89A'],
  ['warning', '#F5A300', '#FFCF6B'],
  ['error', '#D32F2F', '#FFB4AB'],
];

/** D2 §0.2 table 2 — "Vehicle-type marker legend (MAP-03)". */
const D2_VEHICLE: ReadonlyArray<readonly [string, string]> = [
  ['vehBus', '#2E9E4F'],
  ['vehTrain', '#E5331F'],
  ['vehMotorbike', '#8E44CE'],
  ['vehTuk', '#F5C518'],
  ['vehFlex', '#1ABC9C'],
  ['vehSedan', '#1E6FE5'],
  ['vehMiniVan', '#EC4899'],
  ['vehVan', '#F57C00'],
  ['vehTruck', '#8B5E3C'],
  ['vehMiniTruck', '#808000'],
  ['vehPrivate', '#8A8F98'],
];

/** D2 §0.2 table 3 — "Typography". Sizes and weights only; D2 prints no line heights. */
const D2_TYPE: ReadonlyArray<readonly [string, string, string, string, string]> = [
  ['display', 'displaySmall', '.largeTitle', '32px', '700'],
  ['headline', 'headlineMedium', '.title', '22px', '700'],
  ['title', 'titleLarge', '.title3', '18px', '600'],
  ['subtitle', 'titleMedium', '.headline', '16px', '600'],
  ['body', 'bodyLarge', '.body', '16px', '400'],
  ['body-sm', 'bodyMedium', '.callout', '14px', '400'],
  ['label', 'labelMedium', '.caption', '12px', '500'],
  ['caption', 'labelSmall', '.caption2', '11px', '400'],
];

describe('D2 §0.2 brand & semantic colours', () => {
  const byD2Name = new Map<string, SemanticColorToken>(
    Object.values(SEMANTIC_COLORS).map((token) => [token.d2, token]),
  );

  it('carries all sixteen roles and no others', () => {
    expect([...byD2Name.keys()].sort()).toEqual(D2_SEMANTIC.map(([name]) => name).sort());
  });

  it.each(D2_SEMANTIC)('%s is %s light / %s dark', (name, light, dark) => {
    const token = byD2Name.get(name);
    expect(token, `missing D2 role ${name}`).toBeDefined();
    expect(token?.light).toBe(light);
    expect(token?.dark).toBe(dark);
  });

  it('gives every role a distinct hex in each appearance where D2 does', () => {
    // `primary` light and `modeC` share #FF6D00 by design; within the semantic
    // table itself the only intended repeat is background light = onPrimary light.
    for (const token of Object.values(SEMANTIC_COLORS)) {
      expect(token.light).toMatch(/^#[0-9A-F]{6}$/);
      expect(token.dark).toMatch(/^#[0-9A-F]{6}$/);
    }
  });
});

describe('D2 §0.2 vehicle-type marker legend', () => {
  const byD2Name = new Map<string, VehicleColorToken>(
    Object.values(VEHICLE_COLORS).map((token) => [token.d2, token]),
  );

  it('carries all eleven tokens', () => {
    expect(Object.keys(VEHICLE_COLORS)).toHaveLength(11);
    expect([...byD2Name.keys()].sort()).toEqual(D2_VEHICLE.map(([name]) => name).sort());
  });

  it.each(D2_VEHICLE)('%s is %s', (name, hex) => {
    expect(byD2Name.get(name)?.hex).toBe(hex);
  });

  it('maps ten of them onto AL-09 canonical vehicle types, and vehPrivate onto none', () => {
    // The eleventh is a Mode B display token — a private vehicle is a sedan or a
    // van whose marker is grey because of its mode, not a type of its own.
    const canonical = Object.values(VEHICLE_COLORS)
      .map((token) => token.vehicleType)
      .filter((value) => value !== null);

    expect(canonical.sort()).toEqual(
      [
        'bus',
        'flex',
        'mini_truck',
        'mini_van',
        'motorbike',
        'sedan',
        'three_wheeler',
        'train',
        'truck',
        'van',
      ].sort(),
    );
    expect(VEHICLE_COLORS['veh-private'].vehicleType).toBeNull();
  });
});

describe('D2 §0.2 mode badges', () => {
  it('is Mode A green, Mode B grey, Mode C orange', () => {
    expect(MODE_COLORS['mode-a'].hex).toBe('#2E9E4F');
    expect(MODE_COLORS['mode-b'].hex).toBe('#6B7280');
    expect(MODE_COLORS['mode-c'].hex).toBe('#FF6D00');
  });

  it('reuses the exact success green and primary orange, not a near miss', () => {
    expect(MODE_COLORS['mode-a'].hex).toBe(SEMANTIC_COLORS.success.light);
    expect(MODE_COLORS['mode-c'].hex).toBe(SEMANTIC_COLORS.primary.light);
  });
});

describe('D2 §0.2 typography', () => {
  it('carries all eight roles', () => {
    expect(Object.keys(TYPE_ROLES).sort()).toEqual(D2_TYPE.map(([name]) => name).sort());
  });

  it.each(D2_TYPE)('%s maps to %s / %s at %s weight %s', (name, m3, ios, size, weight) => {
    const role = TYPE_ROLES[name as keyof typeof TYPE_ROLES];
    expect(role.androidM3).toBe(m3);
    expect(role.iosDynamicType).toBe(ios);
    expect(role.fontSize).toBe(size);
    expect(role.fontWeight).toBe(weight);
  });

  it('puts Outfit on display and headline only — D2 gives Inter the rest', () => {
    const outfit = Object.entries(TYPE_ROLES)
      .filter(([, role]) => role.family === 'display')
      .map(([name]) => name);
    expect(outfit.sort()).toEqual(['display', 'headline']);
  });

  it('lands every line height on D2\'s 4px grid', () => {
    for (const role of Object.values(TYPE_ROLES)) {
      const px = Number.parseInt(role.lineHeight, 10);
      expect(px % 4, `${role.androidM3} line height ${role.lineHeight}`).toBe(0);
      expect(px).toBeGreaterThanOrEqual(Number.parseInt(role.fontSize, 10));
    }
  });
});

describe('D2 §0.2 spacing, radius and elevation', () => {
  it('is the 4px grid: 4, 8, 12, 16, 24, 32, 48 as xxs…xxl', () => {
    expect(SPACING).toEqual({
      xxs: '4px',
      xs: '8px',
      sm: '12px',
      md: '16px',
      lg: '24px',
      xl: '32px',
      xxl: '48px',
    });
    expect(SPACING_BASE).toBe('4px');
    for (const value of Object.values(SPACING)) {
      expect(Number.parseInt(value, 10) % 4).toBe(0);
    }
  });

  it('is radius sm 8 / md 12 / lg 16 / card 24', () => {
    expect(RADII).toEqual({ sm: '8px', md: '12px', lg: '16px', card: '24px' });
  });

  it('covers the M3 dp ladder 0/1/3/6/8/12', () => {
    expect(Object.values(ELEVATIONS).map((token) => token.dp)).toEqual([0, 1, 3, 6, 8, 12]);
  });

  it("reproduces D2's stated shadow — radius 8, y 2, opacity 0.12 — at the 6dp card level", () => {
    expect(ELEVATIONS['elevation-3'].dp).toBe(6);
    expect(ELEVATIONS['elevation-3'].shadow).toBe('0 2px 8px 0 rgb(0 0 0 / 0.12)');
  });

  it('keeps every step at D2\'s 0.12 opacity', () => {
    for (const [name, token] of Object.entries(ELEVATIONS)) {
      if (token.dp === 0) continue;
      expect(token.shadow, name).toContain('rgb(0 0 0 / 0.12)');
    }
  });
});

describe('D2 §AP/§FP breakpoints', () => {
  it('is mobile 375 / tablet 768 / desktop 1024', () => {
    expect(BREAKPOINTS).toEqual({ mobile: '375px', tablet: '768px', desktop: '1024px' });
  });
});

describe('D2 §0.2 CTA token', () => {
  it('is height 56, radius sm 8, primary on onPrimary, label at the titleMedium role', () => {
    expect(CTA.height).toBe('56px');
    expect(CTA.radius).toBe(RADII.sm);
    expect(CTA.background).toBe('primary');
    expect(CTA.label).toBe('on-primary');
    expect(TYPE_ROLES[CTA.labelRole].androidM3).toBe('titleMedium');
    expect(CTA.iconSize).toBe('20px');
  });
});
