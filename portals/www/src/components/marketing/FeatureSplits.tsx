import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import { Reveal } from '@/components/motion/Reveal';
import { FEATURE_SPLITS } from '@/content/marketing';
import { SCREENS } from '@/content/screens';
import { createWwwTranslator, type Locale } from '@/i18n';

/**
 * Section 4 — five alternating image/text blocks.
 *
 * ## The first block is not revealed, and that is a performance decision
 *
 * S15: *"The first is above the fold on tall viewports — give it `priority` and no
 * reveal transition, or the LCP element animates in and the metric suffers."* Both
 * halves of that matter and they are separate mechanisms:
 *
 *   - **`priority`** makes its image eager and `fetchPriority: high`, so the largest
 *     thing in the viewport is not queued behind four lazy images.
 *   - **No `Reveal`** because the reveal's hidden state is `opacity: 0`. Largest
 *     Contentful Paint is recorded when the element is *painted*, and an element
 *     fading in from zero opacity is not painted until the fade starts — so wrapping
 *     the LCP candidate in a reveal moves the metric by the length of the animation
 *     plus the observer's delay, for no visual gain on an element the reader is
 *     already looking at.
 *
 * The other four are wrapped normally. `Reveal` puts its hidden state inside
 * `@media (scripting: enabled)`, so a reader with JavaScript off is never handed a
 * page of `opacity: 0` — the blocks are simply there.
 *
 * ## Alternating sides, and why it is `md:` and not `sm:`
 *
 * At 375px every block is one column and the image sits above the text; alternating
 * has no meaning in one column. The flip starts at `md:` (768px), where there are
 * two columns to alternate between. D8's three breakpoints, no fourth.
 */
export function FeatureSplits({ locale }: { readonly locale: Locale }) {
  const t = createWwwTranslator(locale);

  return (
    <section className="mx-auto flex max-w-[1200px] flex-col gap-section-lg px-4 py-section">
      {FEATURE_SPLITS.map((feature, index) => {
        const screen = SCREENS.find((entry) => entry.id === feature.screens[0]);
        const first = index === 0;

        const body = (
          <div
            className={cx(
              'grid items-center gap-lg md:grid-cols-2 md:gap-xl',
              // The odd blocks put the image first in the *visual* order only.
              // `order` on the media column and not a reversed DOM, so the reading
              // order stays heading → prose → image at every width.
              index % 2 === 1 && 'md:[&>*:last-child]:order-first',
            )}
          >
            <div className="flex flex-col gap-sm">
              <h2 className="max-w-[20ch] font-display text-hero-sm text-balance text-on-surface">
                {t(feature.headline)}
              </h2>
              <p className="max-w-[54ch] text-body text-on-surface-variant">{t(feature.body)}</p>
            </div>

            {screen ? (
              <ScreenImage
                screen={screen}
                alt={t(screen.captionKey)}
                priority={first}
                sizes="(min-width: 768px) 22rem, 80vw"
                className="mx-auto w-[min(80vw,22rem)]"
              />
            ) : null}
          </div>
        );

        // The LCP candidate renders unwrapped — see the note above.
        return first ? (
          <div key={feature.id}>{body}</div>
        ) : (
          <Reveal key={feature.id} variant="rise">
            {body}
          </Reveal>
        );
      })}
    </section>
  );
}
