/**
 * Chip — the selectable filter chip. D2 radius `sm 8` ("buttons, chips").
 *
 * The `accent` prop takes a vehicle-type or mode token name, which is what makes
 * this reusable for the mode/type filter D2 specifies (SCR-PA-006; "type-filter
 * chips carry a per-type colour token", AL-26). The token is applied through a
 * custom property rather than as a `bg-veh-*` class, because that class would
 * have to be assembled by string concatenation and Tailwind would never see it
 * to compile a rule for. The hex still comes from the preset's token table, so
 * D2 §0.2 remains the only place it is written down.
 */

import type { ComponentProps, CSSProperties, ReactNode } from 'react';

import { MODE_COLORS, VEHICLE_COLORS } from '@mageride/tailwind-preset';

import { cx } from '../lib/cx.js';

export type ChipAccent = keyof typeof VEHICLE_COLORS | keyof typeof MODE_COLORS;

const ACCENT_HEX: Record<string, string> = Object.fromEntries([
  ...Object.entries(VEHICLE_COLORS).map(([name, token]) => [name, token.hex]),
  ...Object.entries(MODE_COLORS).map(([name, token]) => [name, token.hex]),
]);

export interface ChipProps extends ComponentProps<'button'> {
  /** Pressed state, rendered as `aria-pressed` so it reads as a toggle. */
  selected?: boolean;
  /** A vehicle-type or mode token name — the D2 per-type colour. */
  accent?: ChipAccent;
  leadingIcon?: ReactNode;
}

export function Chip({
  selected = false,
  accent,
  leadingIcon,
  className,
  style,
  type = 'button',
  children,
  ...rest
}: ChipProps) {
  const hex = accent ? ACCENT_HEX[accent] : undefined;
  const accentStyle = hex ? ({ '--mr-chip-accent': hex } as CSSProperties) : undefined;

  return (
    <button
      {...rest}
      type={type}
      aria-pressed={selected}
      style={accentStyle ? { ...accentStyle, ...style } : style}
      className={cx(
        'inline-flex items-center gap-xxs rounded-sm border px-sm py-xxs text-label font-body',
        'transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary',
        'disabled:pointer-events-none disabled:opacity-40',
        selected
          ? 'border-primary bg-primary-container text-on-primary-container'
          : 'border-outline bg-surface text-on-surface-variant hover:bg-surface-variant',
        className,
      )}
    >
      {hex ? (
        <span aria-hidden="true" className="block size-xs rounded-full bg-[var(--mr-chip-accent)]" />
      ) : null}
      {leadingIcon ? (
        <span aria-hidden="true" className="flex size-cta-icon shrink-0 items-center justify-center">
          {leadingIcon}
        </span>
      ) : null}
      {children}
    </button>
  );
}
