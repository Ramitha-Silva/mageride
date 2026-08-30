import type { ReactNode } from 'react';

import { cx } from '@mageride/ui';

/**
 * A continuously scrolling strip, from one `@keyframes` and a duplicated track.
 *
 * **A server component.** The animation translates the track by exactly `-50%`,
 * which is one full copy of the items, so the seam falls where the duplicate
 * begins and the loop is invisible. That is the whole trick, and it needs no
 * measurement and no JavaScript.
 *
 * The duplicate is `aria-hidden` and carries `data-mr-marquee-clone`, which the
 * reduced-motion rule uses to remove it — because a *stopped* marquee is a strip
 * whose right-hand half nobody can reach. Under reduced motion the track becomes
 * an ordinary horizontal scroller showing each item once.
 *
 * `items` and not `children`, because the component has to render the list twice
 * and a `ReactNode` cannot be duplicated with stable keys.
 */
export function Marquee({
  items,
  durationSeconds = 40,
  className,
  itemClassName,
}: {
  readonly items: readonly ReactNode[];
  /** One full pass of the track. Longer is slower; the default is a slow drift. */
  readonly durationSeconds?: number;
  readonly className?: string;
  readonly itemClassName?: string;
}) {
  return (
    <div
      className={cx('mr-marquee', className)}
      style={{ '--mr-www-marquee-duration': `${durationSeconds}s` } as React.CSSProperties}
    >
      <div className="mr-marquee-track">
        {items.map((item, index) => (
          <div key={`a-${index}`} className={itemClassName}>
            {item}
          </div>
        ))}
        {items.map((item, index) => (
          <div key={`b-${index}`} aria-hidden data-mr-marquee-clone="true" className={itemClassName}>
            {item}
          </div>
        ))}
      </div>
    </div>
  );
}
