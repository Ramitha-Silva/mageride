import { cx } from '@mageride/ui';

/**
 * The gradient hero backdrop.
 *
 * **A server component with no JavaScript in it at all** — the whole effect is
 * `app/globals.css`'s `.mr-aurora`, compiled by PostCSS at build like every other
 * rule on this surface, which is exactly what AL-52 asks for.
 *
 * The three colours are `primary`, `primary-container` and `secondary-container`
 * read as `--mr-color-*` and mixed toward `transparent` at one declared alpha. No
 * new hex enters the system, and because those are the raw properties the preset
 * flips on `.dark`, the backdrop follows the appearance without a single `dark:`
 * variant.
 *
 * `aria-hidden`, and absolutely positioned: it decorates whatever it is placed
 * inside and must never be the reason a screen reader announces an empty region.
 * The parent needs `relative` and something to stack above it.
 */
export function AuroraBackdrop({ className }: { className?: string }) {
  return <div aria-hidden className={cx('mr-aurora', className)} />;
}
