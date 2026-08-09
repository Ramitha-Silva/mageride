import type { PackageStatus } from '@/api/types';
import type { WebMessageKey, WebTranslator } from '@/i18n';

/**
 * SCR-WT-002's four-step tracker — Pending → Picked → In transit → Delivered
 * (D3' Δ 2026-07-05, US-20.5).
 *
 * **Three of the four are ride states and the fourth is a position**, and that is
 * public-bff's arithmetic rather than this component's: ADD Appendix B.2 invariant
 * 6 is literal — a package traverses the same eighteen states a passenger ride does
 * and adds none — so `PickedUp` and `InTransit` are both `InProgress` as far as
 * `rides.rides` is concerned, and the fact that separates them is whether the
 * vehicle has left the sender. The page draws the step it is given and derives
 * nothing, because a second derivation is a second answer.
 *
 * The dots are `aria-hidden` and the state is carried by one sentence, so a screen
 * reader is told "Step 3 of 4 — In transit" rather than being walked through four
 * decorative circles.
 */

const STEPS: readonly { readonly status: PackageStatus; readonly labelKey: WebMessageKey }[] = [
  { status: 'PickupPending', labelKey: 'web.package.step.pending' },
  { status: 'PickedUp', labelKey: 'web.package.step.picked' },
  { status: 'InTransit', labelKey: 'web.package.step.transit' },
  { status: 'Delivered', labelKey: 'web.package.step.delivered' },
];

export function PackageStepper({ t, status }: { t: WebTranslator; status: PackageStatus }) {
  const current = Math.max(
    0,
    STEPS.findIndex((step) => step.status === status),
  );

  return (
    <section
      aria-label={t('web.package.progress')}
      className="flex flex-col gap-xxs"
    >
      <p className="sr-only">
        {t('web.package.stepOf', { step: current + 1, total: STEPS.length })} —{' '}
        {t(`web.status.${status}`)}
      </p>

      <div aria-hidden="true" className="flex items-center gap-[4px]">
        {STEPS.map((step, index) => (
          <span key={step.status} className="contents">
            {index > 0 ? (
              <span
                className={`h-[2px] flex-1 ${index <= current ? 'bg-success' : 'bg-outline'}`}
              />
            ) : null}
            <span
              className={`block size-[13px] shrink-0 rounded-full ${
                index < current
                  ? 'bg-success'
                  : index === current
                    ? 'bg-primary ring-4 ring-primary/20'
                    : 'bg-outline'
              }`}
            />
          </span>
        ))}
      </div>

      <ol aria-hidden="true" className="flex items-center justify-between gap-xxs">
        {STEPS.map((step, index) => (
          <li
            key={step.status}
            className={`text-caption ${
              index === current ? 'font-semibold text-on-surface' : 'text-on-surface-variant'
            }`}
          >
            {t(step.labelKey)}
          </li>
        ))}
      </ol>
    </section>
  );
}
