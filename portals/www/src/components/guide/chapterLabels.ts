import { CALLOUT_LABEL } from '@/components/guide/Callout';
import { lightboxLabels } from '@/components/showcase/showcaseLabels';
import { DAILY_FEE_TIERS } from '@/content/marketing';
import { SCREENS, type ScreenEntry } from '@/content/screens';
import type { Chapter } from '@/content/types';
import { createWwwTranslator, type Locale } from '@/i18n';

import type { ChapterBodyLabels } from './ChapterBody';

/**
 * The screens a chapter refers to, in step order, deduplicated.
 *
 * The same walk `ChapterBody` does — a screen shown by two steps is one lightbox
 * entry, not two — lifted here so the server and the client agree on the *index* a
 * step's screen has. The lightbox's caption and announcement arrays are keyed by that
 * index, so if the two walks ever disagreed a reader would open one screen and be
 * told the caption of another.
 */
export function referencedScreens(chapter: Chapter): ScreenEntry[] {
  const referenced: ScreenEntry[] = [];
  for (const id of [...chapter.steps.map((step) => step.screenRef), ...chapter.screens]) {
    if (!id || referenced.some((entry) => entry.id === id)) continue;
    const screen = SCREENS.find((entry) => entry.id === id);
    if (screen) referenced.push(screen);
  }
  return referenced;
}

/**
 * Every string one chapter renders, resolved on the server (MCS-36 D3).
 *
 * **One chapter's words, not thirty-four's.** That is the whole saving: `ChapterBody`
 * is a client component, so holding a translator there put both guides in both
 * languages into every page's bundle. This resolves the chapter actually being read.
 *
 * The arrays are index-aligned with `chapter.steps`, including the empty strings —
 * a step with no note still occupies its slot, so `labels.notes[index]` is always the
 * right note and never the next step's.
 */
export function chapterLabels(locale: Locale, chapter: Chapter): ChapterBodyLabels {
  const t = createWwwTranslator(locale);
  const referenced = referencedScreens(chapter);

  const money = new Intl.NumberFormat(`${locale}-LK`, {
    style: 'currency',
    currency: 'LKR',
    currencyDisplay: 'narrowSymbol',
    maximumFractionDigits: 0,
  });

  const screenFor = (ref: string | undefined) =>
    ref ? SCREENS.find((entry) => entry.id === ref) : undefined;

  return {
    stepLabels: chapter.steps.map((_, index) => t('www.guide.stepLabel', { number: index + 1 })),
    instructions: chapter.steps.map((step) => t(step.instruction)),
    notes: chapter.steps.map((step) => (step.note ? t(step.note) : '')),
    openLabels: chapter.steps.map((step) => {
      const screen = screenFor(step.screenRef);
      return screen ? t('www.showcase.open', { caption: t(screen.captionKey) }) : '';
    }),
    stepCaptions: chapter.steps.map((step) => {
      const screen = screenFor(step.screenRef);
      return screen ? t(screen.captionKey) : '';
    }),
    callouts: chapter.callouts.map((callout) => ({
      kind: t(CALLOUT_LABEL[callout.kind]),
      body: t(callout.body),
      sourceLabel: t('www.common.sourceLabel'),
    })),
    /*
     * Present only on the one chapter that renders the table, and the currency
     * formatting happens here too — `Intl.NumberFormat` is a client-side cost that
     * buys nothing when the six values are fixed at build.
     */
    ...(chapter.table === 'daily-fee-tiers'
      ? {
          feeTable: {
            caption: t('www.page.drivers.feeTableHeading'),
            rows: DAILY_FEE_TIERS.map((tier) => ({
              label: t(tier.label),
              amount: money.format(tier.dailyFeeMinor / 100),
            })),
          },
        }
      : {}),
    lightbox: lightboxLabels(locale, referenced),
  };
}
