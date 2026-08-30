'use client';

import { useState } from 'react';

import { cx } from '@mageride/ui';

import { ScreenImage } from '@/components/ScreenImage';
import { StickySteps } from '@/components/motion/StickySteps';
import { SCREENS } from '@/content/screens';

import type { Cut } from './howItWorksLabels';

/**
 * Section 3 — how it works, in four steps, for two audiences.
 *
 * ## Both cuts are in the DOM and switching is a class change
 *
 * S15's requirement, and the reason is crawlability: a passenger cut that only
 * exists after a click is a passenger cut a search engine never indexes, on the one
 * MageRide surface whose purpose is to be found. So both `<ol>`s are rendered and
 * the inactive one is hidden with `hidden`, which removes it from the accessibility
 * tree and from the tab order while leaving it in the markup.
 *
 * `hidden` and not `sr-only`: an off-screen copy of four steps would be read out by
 * a screen reader as a second set of instructions with no way to tell which applies,
 * which is worse than either alternative.
 *
 * ## The toggle is `aria-pressed`, not `role="tab"`
 *
 * S15 asks for `aria-pressed` and it is also the right call. The tab pattern brings
 * obligations this control does not meet and does not need — arrow-key roving focus,
 * `aria-controls` to a `tabpanel`, a `tablist` container — and its promise to a
 * screen-reader user is "these are alternative views of one region". Two toggle
 * buttons promise "one of these two is on", which is what this is. Each carries its
 * own state, so a reader hears "For passengers, toggle button, pressed".
 *
 * ## Reduced motion
 *
 * Handled entirely in `app/globals.css`: the media column stops being `sticky` and
 * every step goes to full opacity, so the section becomes the plain vertical list
 * S15 asks for. **Not** a sticky section with its fade removed, which would leave a
 * reader scrolling past a pinned panel — the same complaint the setting exists to
 * answer. `StickySteps` itself runs no animation in either state; its only
 * JavaScript is the `IntersectionObserver` that marks the active step.
 */
/**
 * The band's heading, its two tab labels, and every step's prose — resolved on the
 * server (MCS-36 D3).
 *
 * The steps are nested per cut because that is how they render: two tab panels, both
 * in the DOM, one hidden. Nesting keeps the shape the component already walks, so the
 * JSX below is unchanged apart from where the strings come from.
 */
export interface HowItWorksLabels {
  readonly heading: string;
  readonly cuts: readonly {
    readonly id: Cut;
    readonly label: string;
    readonly steps: readonly {
      readonly title: string;
      readonly body: string;
      /**
       * The step's screen, still as a *reference* rather than the entry itself.
       *
       * `SCREENS` is a content registry, not a message table — it is already in this
       * bundle and is a tenth the size — so looking the entry up here costs nothing,
       * where serialising 70 screen objects through props would cost real bytes.
       */
      readonly screenRef?: string;
      /** That screen's caption, for `ScreenImage`'s `alt`. */
      readonly caption: string;
    }[];
  }[];
}

export function HowItWorks({ labels }: { readonly labels: HowItWorksLabels }) {
  const [cut, setCut] = useState<Cut>('passenger');

  return (
    <section className="mx-auto max-w-[1200px] px-4 py-section">
      <div className="flex flex-col gap-md sm:flex-row sm:items-center sm:justify-between">
        <h2 className="font-display text-hero-sm text-on-surface">{labels.heading}</h2>

        <div className="flex gap-xxs rounded-lg bg-surface-variant p-xxs">
          {labels.cuts.map((option) => (
            <button
              key={option.id}
              type="button"
              aria-pressed={cut === option.id}
              onClick={() => setCut(option.id)}
              className={cx(
                'min-h-cta rounded-md px-md py-xs text-body-sm font-medium transition-colors',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-secondary',
                cut === option.id
                  ? 'bg-surface text-on-surface shadow-elevation-1'
                  : 'text-on-surface-variant hover:text-on-surface',
              )}
            >
              {option.label}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-lg">
        {labels.cuts.map((option) => (
          <div key={option.id} hidden={cut !== option.id}>
            <StickySteps
              steps={option.steps.map((step, index) => {
                const screen = SCREENS.find((entry) => entry.id === step.screenRef);
                return {
                  id: `${option.id}-${index}`,
                  media: screen ? (
                    <ScreenImage
                      screen={screen}
                      alt={step.caption}
                      sizes="(min-width: 768px) 20rem, 80vw"
                      className="mx-auto w-[min(80vw,20rem)]"
                    />
                  ) : null,
                  body: (
                    <div className="flex flex-col gap-xs">
                      {/*
                        `mr-sticky-step-index` is not decoration — it is what lets
                        `globals.css` take this one ink out of the step's opacity
                        when the step is inactive. `text-secondary` at 0.8 over
                        white is 4.25:1 and fails; see the rule for why the fix is
                        here and not in the opacity.
                      */}
                      <span className="mr-sticky-step-index text-body-sm font-medium text-secondary">
                        {index + 1}
                      </span>
                      <h3 className="font-display text-title text-on-surface">{step.title}</h3>
                      <p className="max-w-[52ch] text-body-sm text-on-surface-variant">
                        {step.body}
                      </p>
                    </div>
                  ),
                };
              })}
            />
          </div>
        ))}
      </div>
    </section>
  );
}
