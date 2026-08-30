import { cx } from '@mageride/ui';

import type { Callout as CalloutData, CalloutKind } from '@/content/types';
import type { WwwMessageKey } from '@/i18n/messages/en';

/**
 * One chapter callout — `tip` / `warning` / `fee` / `privacy`.
 *
 * ## Colour is never the only signal
 *
 * S17's fence, and WCAG **1.4.1** behind it: *"a `fee` callout that is only orange
 * is invisible to a colour-blind reader."* So each kind carries three signals that
 * survive independently —
 *
 *   1. a **text label** ("What this costs"), which is a resource and therefore
 *      reads in the reader's own language;
 *   2. a **glyph**, `aria-hidden` because the label already says it;
 *   3. a border and tint, which is the one that fails first.
 *
 * The label matters twice over in **print**: the print stylesheet drops background
 * colour, and a cheap printer loses the border tint too, so on paper the word is
 * the only thing left. That is also why the label is above the body rather than
 * beside it — a floated label collapses into the prose when the tint goes.
 *
 * ## `fee` and `privacy` show their anchor
 *
 * `Callout.source` is required whenever a callout states a fact, which in practice
 * is every `fee` and every `privacy` one (`types.ts` says so). Those two are the
 * callouts carrying regulated claims — a commercial commitment and a data-protection
 * one — so the anchor renders for the reader, the same way `/vision`'s value cards
 * do. A `tip` describing an interaction has nothing to cite and shows nothing.
 */
/**
 * The word each callout kind is announced by.
 *
 * Exported since MCS-36 D3 so `chapterLabels` can resolve it on the server — the
 * component no longer holds a translator, and this map is the only thing that knew
 * which key a `kind` means.
 */
export const CALLOUT_LABEL: Readonly<Record<CalloutKind, WwwMessageKey>> = {
  tip: 'www.guide.callout.tip',
  warning: 'www.guide.callout.warning',
  fee: 'www.guide.callout.fee',
  privacy: 'www.guide.callout.privacy',
};

/**
 * Tints from the preset's own semantic roles rather than raw hexes, so the callout
 * follows the appearance with no `dark:` variant. `fee` uses `mode-c` — the
 * on-demand badge colour — because a fee on this platform is a Mode C fact, and a
 * reader who has met that orange on the map meets it again here.
 */
const TONE: Readonly<Record<CalloutKind, string>> = {
  tip: 'border-outline bg-surface-variant',
  warning: 'border-error bg-error-container',
  fee: 'border-mode-c bg-primary-container',
  privacy: 'border-mode-a bg-secondary-container',
};

const GLYPH: Readonly<Record<CalloutKind, string>> = {
  tip: '💡',
  warning: '⚠',
  fee: '₨',
  privacy: '🔒',
};

/** One callout's three strings, resolved on the server (MCS-36 D3). */
export interface CalloutLabels {
  /** "Note" / "Warning" / "Tip" — the kind's own word. */
  readonly kind: string;
  readonly body: string;
  /** The "Source:" prefix, present only when the callout cites one. */
  readonly sourceLabel: string;
}

export function Callout({
  labels,
  callout,
  className: _className,
}: {
  readonly labels: CalloutLabels;
  readonly callout: CalloutData;
  readonly className?: string;
}) {
  return (
    <aside
      className={cx(
        'flex flex-col gap-xs rounded-card border-l-4 p-lg print-plain',
        TONE[callout.kind],
      )}
    >
      <p className="flex items-center gap-xs text-body-sm font-bold text-on-surface">
        <span aria-hidden>{GLYPH[callout.kind]}</span>
        {labels.kind}
      </p>
      <p className="text-body-sm text-on-surface">{labels.body}</p>
      {callout.source ? (
        <p className="text-body-sm text-on-surface-variant">
          <span className="font-medium">{labels.sourceLabel}</span>{' '}
          <span className="break-all font-mono text-[0.75em]">{callout.source}</span>
        </p>
      ) : null}
    </aside>
  );
}
