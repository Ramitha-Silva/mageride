/**
 * The `tailwind.config` preset AL-52 names — D2 §0.2 mapped into `theme.extend`
 * (plus `screens`, which AL-52 says the D2 breakpoints *become*).
 *
 * Consume it from a surface that keeps a JS config:
 *
 *   // tailwind.config.js
 *   import { mageridePreset } from '@mageride/tailwind-preset';
 *   export default { presets: [mageridePreset], content: [...] };
 *
 *   /* app.css *\/
 *   @import "tailwindcss";
 *   @config "../tailwind.config.js";
 *
 * On Tailwind v4 the CSS entry point (`theme.css`) is the better of the two and
 * is what the portals use: a JS config's `screens` merges with v4's built-in
 * breakpoints rather than replacing them, so `sm:` would keep its 640px
 * meaning. Everything else behaves identically, and `test/parity.test.ts`
 * asserts value-for-value that this object and `theme.css` carry the same D2
 * tokens.
 */

import {
  BREAKPOINTS,
  BREAKPOINT_ALIASES,
  CTA,
  ELEVATIONS,
  ELEVATION_ALIASES,
  FONT_FAMILIES,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  TYPE_ROLES,
  VEHICLE_COLORS,
  semanticColorVar,
  type SemanticColorName,
} from './tokens.js';

/** A Tailwind `fontSize` entry: `[size, { lineHeight, fontWeight }]`. */
export type FontSizeEntry = readonly [string, { readonly lineHeight: string; readonly fontWeight: string }];

export interface MageridePreset {
  readonly darkMode: 'class';
  readonly theme: {
    readonly screens: Record<string, string>;
    readonly extend: {
      readonly colors: Record<string, string>;
      readonly fontFamily: Record<string, string[]>;
      readonly fontSize: Record<string, FontSizeEntry>;
      readonly spacing: Record<string, string>;
      readonly borderRadius: Record<string, string>;
      readonly boxShadow: Record<string, string>;
    };
  };
}

function colors(): Record<string, string> {
  const out: Record<string, string> = {};
  // Semantic roles point at the raw appearance variable, never at a hex: that
  // is what makes one class on <html> flip all sixteen. The hexes themselves
  // live in theme.css's :root/.dark blocks, which a JS-config surface still
  // imports.
  for (const name of Object.keys(SEMANTIC_COLORS) as SemanticColorName[]) {
    out[name] = `var(${semanticColorVar(name)})`;
  }
  for (const [name, token] of Object.entries(VEHICLE_COLORS)) out[name] = token.hex;
  for (const [name, token] of Object.entries(MODE_COLORS)) out[name] = token.hex;
  return out;
}

function fontSize(): Record<string, FontSizeEntry> {
  const out: Record<string, FontSizeEntry> = {};
  for (const [name, role] of Object.entries(TYPE_ROLES)) {
    out[name] = [role.fontSize, { lineHeight: role.lineHeight, fontWeight: role.fontWeight }];
  }
  return out;
}

function spacing(): Record<string, string> {
  return { ...SPACING, cta: CTA.height, 'cta-icon': CTA.iconSize };
}

function boxShadow(): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [name, token] of Object.entries(ELEVATIONS)) out[name] = token.shadow;
  for (const [name, shadow] of Object.entries(ELEVATION_ALIASES)) out[name] = shadow;
  return out;
}

/** Splits a CSS font stack back into the array shape Tailwind's config expects. */
function stack(value: string): string[] {
  // `var(--mr-font-outfit, 'Outfit')` contains no comma, so a plain split is safe;
  // a guard keeps it that way if the fallback ever grows one.
  if (/var\([^)]*,[^)]*,/.test(value)) {
    throw new Error(`font stack has a multi-argument var() this splitter cannot handle: ${value}`);
  }
  return value.split(/,\s*(?![^(]*\))/).map((part) => part.trim());
}

export const mageridePreset: MageridePreset = {
  darkMode: 'class',
  theme: {
    // Not `extend.screens` — AL-52 makes the D2 three the screens config.
    screens: { ...BREAKPOINTS, ...BREAKPOINT_ALIASES },
    extend: {
      colors: colors(),
      fontFamily: { display: stack(FONT_FAMILIES.display), body: stack(FONT_FAMILIES.body) },
      fontSize: fontSize(),
      spacing: spacing(),
      borderRadius: { ...RADII },
      boxShadow: boxShadow(),
    },
  },
};

export default mageridePreset;
