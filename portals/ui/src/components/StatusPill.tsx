/**
 * StatusPill — the Pending / Approved / Rejected chip that runs through the
 * verification queue (SCR-AP-003), the fleet vehicle table (SCR-FP-004) and the
 * GTFS version history (SCR-AP-016).
 *
 * Tones are drawn from the three D2 semantic status colours plus the neutral
 * surface roles. D2 gives `success`/`warning`/`error` but no matching "on-"
 * foreground, so the pill is the status colour at 12% over the surface with the
 * status colour as the text — which needs no token D2 does not define and holds
 * its contrast in both appearances, where a fixed light tint would not.
 *
 * The label is a prop. Both the tone and the wording of a status are decisions
 * the calling screen makes from its own resource strings; a pill that mapped
 * `"approved"` to English text would be untranslatable by construction.
 */

import type { ComponentProps, ReactNode } from 'react';

import { cx } from '../lib/cx.js';

export type StatusTone = 'neutral' | 'info' | 'success' | 'warning' | 'error';

const TONES: Record<StatusTone, string> = {
  neutral: 'bg-surface-variant text-on-surface-variant border-outline',
  info: 'bg-secondary/12 text-secondary border-secondary/30',
  success: 'bg-success/12 text-success border-success/30',
  warning: 'bg-warning/12 text-warning border-warning/30',
  error: 'bg-error/12 text-error border-error/30',
};

export interface StatusPillProps extends ComponentProps<'span'> {
  tone?: StatusTone;
  /** A small leading dot in the tone colour. On by default — it is the fastest scan cue in a table. */
  dot?: boolean;
  children: ReactNode;
}

export function StatusPill({
  tone = 'neutral',
  dot = true,
  className,
  children,
  ...rest
}: StatusPillProps) {
  return (
    <span
      {...rest}
      className={cx(
        'inline-flex items-center gap-xxs rounded-sm border px-xs py-[2px] text-label font-body whitespace-nowrap',
        TONES[tone],
        className,
      )}
    >
      {dot ? <span aria-hidden="true" className="block size-xxs rounded-full bg-current" /> : null}
      {children}
    </span>
  );
}
