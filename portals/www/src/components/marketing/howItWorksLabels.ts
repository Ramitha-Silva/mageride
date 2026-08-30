import {
  HOW_IT_WORKS_DRIVER,
  HOW_IT_WORKS_PASSENGER,
  type HowItWorksStep,
} from '@/content/marketing';
import { HOME } from '@/content/pages';
import { SCREENS } from '@/content/screens';
import { createWwwTranslator, type Locale, type WwwMessageKey } from '@/i18n';

import type { HowItWorksLabels } from './HowItWorks';

/** Which of the two readings the band is showing. */
export type Cut = 'passenger' | 'driver';

/**
 * The two cuts, and the content each one walks.
 *
 * Moved here from `HowItWorks.tsx` in MCS-36 D3: the component no longer touches the
 * content registries at all, so the table that joins a tab to its steps belongs on
 * the server side of the boundary with everything else it feeds.
 */
const CUTS: readonly {
  readonly id: Cut;
  readonly labelKey: WwwMessageKey;
  readonly steps: readonly HowItWorksStep[];
}[] = [
  { id: 'passenger', labelKey: HOME.how.passengerTab, steps: HOW_IT_WORKS_PASSENGER },
  { id: 'driver', labelKey: HOME.how.driverTab, steps: HOW_IT_WORKS_DRIVER },
];

/**
 * The "how it works" band's strings, resolved on the server (MCS-36 D3).
 *
 * Both cuts, always — the component renders two tab panels and hides one rather than
 * unmounting it, so a reader with JavaScript off can still read the other, and a
 * crawler sees both. Resolving only the selected cut would quietly undo that.
 */
export function howItWorksLabels(locale: Locale): HowItWorksLabels {
  const t = createWwwTranslator(locale);

  return {
    heading: t(HOME.how.heading),
    cuts: CUTS.map((option) => ({
      id: option.id,
      label: t(option.labelKey),
      steps: option.steps.map((step) => {
        const screen = SCREENS.find((entry) => entry.id === step.screenRef);
        return {
          title: t(step.title),
          body: t(step.body),
          screenRef: step.screenRef,
          caption: screen ? t(screen.captionKey) : '',
        };
      }),
    })),
  };
}
