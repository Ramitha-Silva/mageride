import { cx } from '@mageride/ui';

import { createWwwTranslator, type AnyMessageKey, type Locale } from '@/i18n';

/**
 * A route drawing itself between two markers — inline SVG, `stroke-dasharray` and
 * `@keyframes`, and **no JavaScript**.
 *
 * A server component, which is the point: the obvious implementation measures the
 * path with `getTotalLength()` and writes the result into a style, and that is a
 * client component, a layout read and a runtime style write for something the
 * author already knows. `--mr-www-route-length` is authored beside the path
 * instead. It only has to be **at least** the path's true length for the effect to
 * be correct, so it is a safe over-estimate rather than a measurement.
 *
 * **This is not a map.** MCS-34's fourth negative forbids one — no MapLibre, no
 * tiles, no basemap request — so this is an illustration of a journey, drawn from
 * two coordinates that are in the source file. Nothing here reaches the platform,
 * and nothing here should ever be made to.
 *
 * The two colours are D2 §0.2 roles: the line is `primary` (Mode C's own colour)
 * and the pulse rings are the same, so a change to the brand reaches this drawing
 * with no edit.
 */
export function RouteDraw({
  locale,
  labelKey,
  className,
}: {
  readonly locale: Locale;
  /** A key, not a string — this drawing means something different on each page. */
  readonly labelKey: AnyMessageKey;
  readonly className?: string;
}) {
  const t = createWwwTranslator(locale);

  return (
    <svg
      viewBox="0 0 320 180"
      role="img"
      className={cx('h-auto w-full', className)}
      style={{ '--mr-www-route-length': '360' } as React.CSSProperties}
    >
      <title>{t(labelKey)}</title>

      {/* The road under the route — `outline-variant`, so it reads as ground. */}
      <path
        d="M 24 148 C 96 148, 88 64, 160 64 S 232 32, 296 32"
        fill="none"
        stroke="var(--mr-color-outline-variant)"
        strokeWidth="2"
        strokeLinecap="round"
        opacity="0.35"
      />

      {/* …and the route drawn along it. */}
      <path
        className="mr-route-line"
        d="M 24 148 C 96 148, 88 64, 160 64 S 232 32, 296 32"
        fill="none"
        stroke="var(--mr-color-primary)"
        strokeWidth="4"
        strokeLinecap="round"
      />

      {/* Origin: a filled point with a ring pulsing out of it. */}
      <circle className="mr-route-marker" cx="24" cy="148" r="10" fill="var(--mr-color-primary)" />
      <circle cx="24" cy="148" r="5" fill="var(--mr-color-primary)" />

      {/* Destination: the same, half a cycle later so the two do not beat together. */}
      <circle
        className="mr-route-marker"
        cx="296"
        cy="32"
        r="10"
        fill="var(--mr-color-primary)"
        style={{ animationDelay: '1200ms' }}
      />
      <circle cx="296" cy="32" r="5" fill="var(--mr-color-primary)" />
    </svg>
  );
}
