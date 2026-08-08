/**
 * Button — and, in its default shape, the D2 §0.2 **CTA token**.
 *
 * D2: "CTA token (replaces NY `PrimaryButton`): height 56dp, radius sm 8,
 * `primary` bg, `onPrimary` label `titleMedium`, optional 20dp leading/trailing
 * icon, ripple/`.buttonStyle` press state, inline lottie/`ProgressView` loader."
 *
 * `variant="primary" size="cta"` — the defaults — emit exactly the class list
 * the preset publishes as `CTA_CLASS_NAMES`, which is why that constant lives in
 * `@mageride/tailwind-preset` beside the token rather than here: the smoke page
 * and this component read the same declaration, so the compiled stylesheet
 * always contains the rules this component relies on.
 *
 * The other variants and the compact size are not D2 tokens. They exist because
 * a back-office table row cannot carry a 56px button; they are built from D2
 * tokens only, never from new colours.
 */

import type { ComponentProps, ReactNode } from 'react';

import { CTA_CLASS_NAMES } from '@mageride/tailwind-preset';

import { cx } from '../lib/cx.js';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'cta' | 'compact';

const VARIANTS: Record<ButtonVariant, string> = {
  // D2's CTA colours: `primary` background, `onPrimary` label.
  primary: 'bg-primary text-on-primary hover:bg-primary/90 active:bg-primary/80',
  secondary:
    'bg-secondary-container text-on-surface border border-outline hover:bg-secondary-container/80',
  ghost: 'bg-transparent text-on-surface hover:bg-surface-variant',
  danger: 'bg-error text-white hover:bg-error/90 active:bg-error/80',
};

const SIZES: Record<ButtonSize, string> = {
  // 56dp and radius sm 8 — the D2 CTA geometry.
  cta: 'h-cta px-lg rounded-sm text-subtitle',
  // 40px — ten units of D2's 4px grid. Not a named token; D2 names only the CTA.
  compact: 'h-10 px-md rounded-sm text-body-sm',
};

const BASE = [
  'inline-flex items-center justify-center gap-xs font-body',
  'select-none transition-colors',
  'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary',
  'disabled:pointer-events-none disabled:opacity-40',
].join(' ');

export interface ButtonProps extends Omit<ComponentProps<'button'>, 'children'> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** D2's optional 20dp leading icon. */
  leadingIcon?: ReactNode;
  /** D2's optional 20dp trailing icon. */
  trailingIcon?: ReactNode;
  /**
   * D2's "inline lottie/`ProgressView` loader". While busy the button is
   * disabled and marked `aria-busy`, and the label stays put — a spinner that
   * replaces the label makes the button resize under the pointer.
   */
  busy?: boolean;
  /**
   * The accessible name for the busy state. Required rather than defaulted:
   * a default would be a hardcoded English string, which the trilingual rule
   * (CLAUDE.md) forbids.
   */
  busyLabel?: string;
  children?: ReactNode;
}

/** The 20dp box D2 gives a CTA icon. Applied to both slots so they align. */
function IconSlot({ children }: { children: ReactNode }) {
  return (
    <span aria-hidden="true" className="flex size-cta-icon shrink-0 items-center justify-center">
      {children}
    </span>
  );
}

export function Button({
  variant = 'primary',
  size = 'cta',
  leadingIcon,
  trailingIcon,
  busy = false,
  busyLabel,
  className,
  disabled,
  type = 'button',
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      disabled={disabled ?? busy}
      aria-busy={busy || undefined}
      className={cx(BASE, VARIANTS[variant], SIZES[size], className)}
    >
      {busy ? (
        <IconSlot>
          <span className="size-cta-icon animate-spin rounded-full border-2 border-current border-t-transparent" />
        </IconSlot>
      ) : (
        leadingIcon && <IconSlot>{leadingIcon}</IconSlot>
      )}
      {children}
      {busy && busyLabel ? <span className="sr-only">{busyLabel}</span> : null}
      {!busy && trailingIcon ? <IconSlot>{trailingIcon}</IconSlot> : null}
    </button>
  );
}

/**
 * The class list D2's CTA token compiles to, re-exported from the preset so a
 * surface that needs a CTA-shaped anchor (`<a>` cannot be a `<button>`) gets the
 * same geometry without copying it.
 */
export { CTA_CLASS_NAMES };
