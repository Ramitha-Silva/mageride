/**
 * D2' §0.2 — MageRide Design Tokens, transcribed for the web.
 *
 * Spec: `specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens`
 *       (AUTHORITATIVE — single source of truth for Figma + Compose + SwiftUI)
 * ADD:  AL-52 (`specs/architecture-design-document.md#1-15-remediation-log-add-v3-3`)
 *
 * This module is the ONLY place a D2 §0.2 value is spelled on the web. Both build
 * outputs — `dist/theme.css` (the Tailwind v4 `@theme` layer every surface imports)
 * and `dist/preset.js` (the `tailwind.config` preset AL-52 names) — are generated
 * from it, so the two cannot disagree with each other or with the spec.
 *
 * Every entry carries its D2 token name (`d2`) alongside the Tailwind utility name,
 * because the D2 table is camelCase (Compose/SwiftUI) and Tailwind utilities are
 * kebab-case. `test/tokens.test.ts` walks the D2 names, not the utility names.
 */

/** A D2 semantic colour role — one hex per appearance. */
export interface SemanticColorToken {
  /** The D2 §0.2 token name (Compose `MaterialTheme.colorScheme` role). */
  readonly d2: string;
  /** D2 "Light hex". */
  readonly light: string;
  /** D2 "Dark hex". */
  readonly dark: string;
}

/** A D2 vehicle-type marker token (MAP-03 legend). One hex, both appearances. */
export interface VehicleColorToken {
  readonly d2: string;
  readonly hex: string;
  /** The AL-09 canonical `registry.vehicles.vehicle_type`, or `null` for a display-only token. */
  readonly vehicleType: string | null;
  /** D2's plain-language colour name, kept so the legend stays legible. */
  readonly name: string;
}

/** A D2 §0.2 type-scale role. Sizes are the shared design contract across platforms. */
export interface TypeRoleToken {
  /** The Material 3 token D2 maps this role onto (Android). */
  readonly androidM3: string;
  /** The iOS Dynamic Type role D2 maps this role onto. */
  readonly iosDynamicType: string;
  readonly fontSize: string;
  readonly lineHeight: string;
  readonly fontWeight: string;
  /** Which of the two families carries the role — D2: Outfit = display/headline, Inter = body. */
  readonly family: 'display' | 'body';
}

/** A D2 elevation step. Android M3 dp level → the web shadow that reproduces it. */
export interface ElevationToken {
  /** The Android M3 `surfaceColorAtElevation` dp level D2 lists. */
  readonly dp: number;
  readonly shadow: string;
}

// ---------------------------------------------------------------------------
// Brand & semantic colours (D2 §0.2 table 1) — light → dark variant.
// ---------------------------------------------------------------------------

export const SEMANTIC_COLORS = {
  'primary': { d2: 'primary', light: '#FF6D00', dark: '#FFB68A' },
  'on-primary': { d2: 'onPrimary', light: '#FFFFFF', dark: '#4A2300' },
  'primary-container': { d2: 'primaryContainer', light: '#FFE0CC', dark: '#6A3500' },
  'on-primary-container': { d2: 'onPrimaryContainer', light: '#2B1100', dark: '#FFDCC4' },
  'secondary': { d2: 'secondary', light: '#0061A4', dark: '#9FCAFF' },
  'secondary-container': { d2: 'secondaryContainer', light: '#D1E4FF', dark: '#00497D' },
  'background': { d2: 'background', light: '#FFFFFF', dark: '#121316' },
  'surface': { d2: 'surface', light: '#F7F8FA', dark: '#1A1C1E' },
  'surface-variant': { d2: 'surfaceVariant', light: '#ECEEF1', dark: '#2A2D31' },
  'outline': { d2: 'outline', light: '#C7CBD1', dark: '#43474E' },
  'on-surface': { d2: 'onSurface', light: '#1A1C1E', dark: '#E3E2E6' },
  'on-surface-variant': { d2: 'onSurfaceVariant', light: '#44474B', dark: '#C3C7CF' },
  'outline-variant': { d2: 'outlineVariant', light: '#74777C', dark: '#8D9199' },
  'success': { d2: 'success', light: '#2E9E4F', dark: '#7FD89A' },
  'warning': { d2: 'warning', light: '#F5A300', dark: '#FFCF6B' },
  'error': { d2: 'error', light: '#D32F2F', dark: '#FFB4AB' },
} as const satisfies Record<string, SemanticColorToken>;

// ---------------------------------------------------------------------------
// Vehicle-type marker legend (D2 §0.2 table 2; MAP-03, AL-09) — 11 tokens.
//
// Ten of the eleven are canonical `registry.vehicles.vehicle_type` values
// (backend/src/Registry.Api/Domain/VehicleTypes.cs). `vehPrivate` is the
// eleventh: a Mode B *display* token, not a vehicle type — a private vehicle
// is a `sedan`/`van`/… whose marker is drawn grey because of its mode.
// ---------------------------------------------------------------------------

export const VEHICLE_COLORS = {
  'veh-bus': { d2: 'vehBus', hex: '#2E9E4F', vehicleType: 'bus', name: 'green' },
  'veh-train': { d2: 'vehTrain', hex: '#E5331F', vehicleType: 'train', name: 'red' },
  'veh-motorbike': { d2: 'vehMotorbike', hex: '#8E44CE', vehicleType: 'motorbike', name: 'purple' },
  'veh-tuk': { d2: 'vehTuk', hex: '#F5C518', vehicleType: 'three_wheeler', name: 'yellow' },
  'veh-flex': { d2: 'vehFlex', hex: '#1ABC9C', vehicleType: 'flex', name: 'teal' },
  'veh-sedan': { d2: 'vehSedan', hex: '#1E6FE5', vehicleType: 'sedan', name: 'blue' },
  'veh-mini-van': { d2: 'vehMiniVan', hex: '#EC4899', vehicleType: 'mini_van', name: 'pink' },
  'veh-van': { d2: 'vehVan', hex: '#F57C00', vehicleType: 'van', name: 'orange' },
  'veh-truck': { d2: 'vehTruck', hex: '#8B5E3C', vehicleType: 'truck', name: 'brown' },
  'veh-mini-truck': { d2: 'vehMiniTruck', hex: '#808000', vehicleType: 'mini_truck', name: 'olive' },
  'veh-private': { d2: 'vehPrivate', hex: '#8A8F98', vehicleType: null, name: 'grey' },
} as const satisfies Record<string, VehicleColorToken>;

// ---------------------------------------------------------------------------
// Mode badges (D2 §0.2). Mode A = green · Mode B = grey · Mode C = orange.
// ---------------------------------------------------------------------------

export const MODE_COLORS = {
  'mode-a': { d2: 'modeA', hex: '#2E9E4F', mode: 'A' },
  'mode-b': { d2: 'modeB', hex: '#6B7280', mode: 'B' },
  'mode-c': { d2: 'modeC', hex: '#FF6D00', mode: 'C' },
} as const satisfies Record<string, { d2: string; hex: string; mode: 'A' | 'B' | 'C' }>;

// ---------------------------------------------------------------------------
// Typography (D2 §0.2 table 3).
//
// D2 fixes the *sizes* and *weights* ("Sizes are the shared design contract")
// and names the families — Outfit for display/headline, Inter for body — but
// prints no line heights, because Compose and SwiftUI derive them from the
// platform type scale. The web has no such derivation, so the line heights
// below are this component's addition: the Material 3 line height for the
// mapped `androidM3` token, re-quantised onto D2's own 4 px grid. Recorded as
// a spec gap in build/progress.md rather than passed off as spec'd values.
// ---------------------------------------------------------------------------

export const TYPE_ROLES = {
  'display': {
    androidM3: 'displaySmall',
    iosDynamicType: '.largeTitle',
    fontSize: '32px',
    lineHeight: '40px',
    fontWeight: '700',
    family: 'display',
  },
  'headline': {
    androidM3: 'headlineMedium',
    iosDynamicType: '.title',
    fontSize: '22px',
    lineHeight: '28px',
    fontWeight: '700',
    family: 'display',
  },
  'title': {
    androidM3: 'titleLarge',
    iosDynamicType: '.title3',
    fontSize: '18px',
    lineHeight: '24px',
    fontWeight: '600',
    family: 'body',
  },
  'subtitle': {
    androidM3: 'titleMedium',
    iosDynamicType: '.headline',
    fontSize: '16px',
    lineHeight: '24px',
    fontWeight: '600',
    family: 'body',
  },
  'body': {
    androidM3: 'bodyLarge',
    iosDynamicType: '.body',
    fontSize: '16px',
    lineHeight: '24px',
    fontWeight: '400',
    family: 'body',
  },
  'body-sm': {
    androidM3: 'bodyMedium',
    iosDynamicType: '.callout',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: '400',
    family: 'body',
  },
  'label': {
    androidM3: 'labelMedium',
    iosDynamicType: '.caption',
    fontSize: '12px',
    lineHeight: '16px',
    fontWeight: '500',
    family: 'body',
  },
  'caption': {
    androidM3: 'labelSmall',
    iosDynamicType: '.caption2',
    fontSize: '11px',
    lineHeight: '16px',
    fontWeight: '400',
    family: 'body',
  },
} as const satisfies Record<string, TypeRoleToken>;

/**
 * The two families D2 names. Each resolves an optional CSS variable first so a
 * Next.js surface can hand the self-hosted `next/font` face straight in
 * (`Outfit({ variable: '--mr-font-outfit' })`) without redefining the token;
 * with no variable set the plain family name and the system stack take over.
 * No webfont is fetched at runtime — AL-52's CSP posture forbids a CDN link.
 */
export const FONT_FAMILIES = {
  display: "var(--mr-font-outfit, 'Outfit'), ui-sans-serif, system-ui, sans-serif",
  body: "var(--mr-font-inter, 'Inter'), ui-sans-serif, system-ui, sans-serif",
} as const;

// ---------------------------------------------------------------------------
// Spacing (D2 §0.2: "4px base grid: 4, 8, 12, 16, 24, 32, 48 → xxs…xxl").
//
// The grid itself is Tailwind's own `--spacing` (0.25rem = 4 px), which is why
// the numeric scale (`p-1`, `gap-6`, …) is left intact — it *is* D2's 4 px
// grid. The seven named steps are added on top.
// ---------------------------------------------------------------------------

export const SPACING = {
  xxs: '4px',
  xs: '8px',
  sm: '12px',
  md: '16px',
  lg: '24px',
  xl: '32px',
  xxl: '48px',
} as const;

/** D2 §0.2 base grid unit — Tailwind's `--spacing`, spelled here so a test can bind them. */
export const SPACING_BASE = '4px';

// ---------------------------------------------------------------------------
// Corner radius (D2 §0.2): sm 8 (buttons, chips) · md 12 (fields, sheet tops)
// · lg 16 (modals) · card 24 (elevated cards, bottom sheets).
// ---------------------------------------------------------------------------

export const RADII = {
  sm: '8px',
  md: '12px',
  lg: '16px',
  card: '24px',
} as const;

// ---------------------------------------------------------------------------
// Elevation (D2 §0.2): Android M3 levels 0/1/3/6/8/12dp; iOS "subtle shadows
// (radius 8, y 2, opacity 0.12)".
//
// The web gets neither `surfaceColorAtElevation` nor UIKit materials, so the
// ladder is rendered as box-shadows anchored on the one shadow D2 states
// outright: `elevation-3` (the 6 dp card level) IS `0 2px 8px / 0.12`. The
// other five scale from it linearly in dp — y = dp/3, blur = 4·dp/3, blur
// rounded to the nearest even px — which reproduces D2's recipe exactly at
// 6 dp and degrades sensibly either side of it.
// ---------------------------------------------------------------------------

export const ELEVATIONS = {
  'elevation-0': { dp: 0, shadow: '0 0 #0000' },
  'elevation-1': { dp: 1, shadow: '0 1px 2px 0 rgb(0 0 0 / 0.12)' },
  'elevation-2': { dp: 3, shadow: '0 1px 4px 0 rgb(0 0 0 / 0.12)' },
  'elevation-3': { dp: 6, shadow: '0 2px 8px 0 rgb(0 0 0 / 0.12)' },
  'elevation-4': { dp: 8, shadow: '0 3px 10px 0 rgb(0 0 0 / 0.12)' },
  'elevation-5': { dp: 12, shadow: '0 4px 16px 0 rgb(0 0 0 / 0.12)' },
} as const satisfies Record<string, ElevationToken>;

/** D2 pairs `card 24` radius with "elevated cards" — the 6 dp step. */
export const ELEVATION_ALIASES = {
  card: ELEVATIONS['elevation-3'].shadow,
} as const;

// ---------------------------------------------------------------------------
// Breakpoints — D2 §AP/§FP "mobile 375px / tablet 768px / desktop 1024px".
//
// AL-52: "D2 breakpoints (375/768/1024) map to Tailwind `screens`". These
// REPLACE Tailwind's defaults rather than extending them; `sm`/`md`/`lg` are
// re-pointed at the same three values so idiomatic class names keep working
// and cannot silently mean 640px.
// ---------------------------------------------------------------------------

export const BREAKPOINTS = {
  mobile: '375px',
  tablet: '768px',
  desktop: '1024px',
} as const;

export const BREAKPOINT_ALIASES = {
  sm: BREAKPOINTS.mobile,
  md: BREAKPOINTS.tablet,
  lg: BREAKPOINTS.desktop,
} as const;

// ---------------------------------------------------------------------------
// CTA (D2 §0.2, "replaces NY PrimaryButton"): height 56dp, radius sm 8,
// `primary` bg, `onPrimary` label at `titleMedium` (= the `subtitle` role),
// optional 20dp leading/trailing icon.
//
// Only the height needs a token of its own — the other four are the tokens
// above, and `@mageride/ui`'s Button composes them. The icon box is included
// because "20dp" is a number no other token carries.
// ---------------------------------------------------------------------------

export const CTA = {
  height: '56px',
  radius: RADII.sm,
  background: 'primary',
  label: 'on-primary',
  labelRole: 'subtitle',
  iconSize: '20px',
} as const;

/**
 * Keyword colours Tailwind needs once `--color-*` is reset. Not D2 tokens —
 * `transparent`/`currentColor`/`inherit` are CSS keywords, and `white`/`black`
 * are the two absolutes D2's light and dark backgrounds already contain
 * (`background` light `#FFFFFF`).
 */
export const KEYWORD_COLORS = {
  inherit: 'inherit',
  current: 'currentColor',
  transparent: 'transparent',
  white: '#FFFFFF',
  black: '#000000',
} as const;

/** Prefix for the raw CSS custom properties the light/dark pairs are published under. */
export const CSS_VAR_PREFIX = '--mr';

/** The class Tailwind's `dark:` variant keys off (`darkMode: 'class'`). */
export const DARK_CLASS = 'dark';

export type SemanticColorName = keyof typeof SEMANTIC_COLORS;
export type VehicleColorName = keyof typeof VEHICLE_COLORS;
export type ModeColorName = keyof typeof MODE_COLORS;
export type TypeRoleName = keyof typeof TYPE_ROLES;
export type SpacingName = keyof typeof SPACING;
export type RadiusName = keyof typeof RADII;
export type ElevationName = keyof typeof ELEVATIONS;
export type BreakpointName = keyof typeof BREAKPOINTS;

/** The name of the CSS custom property a semantic role is published under. */
export function semanticColorVar(name: SemanticColorName): string {
  return `${CSS_VAR_PREFIX}-color-${name}`;
}
