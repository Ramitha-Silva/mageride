/**
 * Class composition for the primitives.
 *
 * A component that takes `className` has to let the caller win: `<Button
 * className="bg-error">` must actually be red. Tailwind cannot do that by
 * source order — the class attribute order is irrelevant, only the order of the
 * rules in the compiled stylesheet counts — so conflicting utilities are
 * resolved here instead, last-one-wins.
 *
 * `tailwind-merge` needs to know which utilities conflict, and it only knows
 * about Tailwind's own scales. The MageRide theme is taught to it from
 * `@mageride/tailwind-preset` directly, so a token added to D2 §0.2 is
 * understood here the moment it lands, with nothing to keep in step by hand.
 *
 * This is class-string arithmetic, not CSS-in-JS: nothing is injected at
 * runtime and the stylesheet is still the one PostCSS compiled (AL-52).
 */

import {
  CTA,
  ELEVATIONS,
  ELEVATION_ALIASES,
  MODE_COLORS,
  RADII,
  SEMANTIC_COLORS,
  SPACING,
  TYPE_ROLES,
  VEHICLE_COLORS,
} from '@mageride/tailwind-preset';
import { extendTailwindMerge } from 'tailwind-merge';

const colorTokens = [
  ...Object.keys(SEMANTIC_COLORS),
  ...Object.keys(VEHICLE_COLORS),
  ...Object.keys(MODE_COLORS),
];

const spacingTokens = [...Object.keys(SPACING), 'cta', 'cta-icon'];

const shadowTokens = [...Object.keys(ELEVATIONS), ...Object.keys(ELEVATION_ALIASES)];

const merge = extendTailwindMerge({
  extend: {
    theme: {
      color: colorTokens,
      spacing: spacingTokens,
      radius: Object.keys(RADII),
      text: Object.keys(TYPE_ROLES),
      shadow: shadowTokens,
      font: ['display', 'body'],
    },
  },
});

/** Falsy entries are dropped, so `cond && 'class'` reads naturally. */
export type ClassValue = string | false | null | undefined;

/** Joins class names and resolves Tailwind conflicts in favour of the last one. */
export function cx(...values: ClassValue[]): string {
  return merge(values.filter(Boolean).join(' '));
}

/** The D2 CTA control height, re-exported so a caller can size a sibling to it. */
export const CTA_HEIGHT = CTA.height;
