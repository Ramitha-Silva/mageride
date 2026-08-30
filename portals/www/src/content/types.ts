/**
 * The shape of a guide chapter — defined once, here, and conformed to by every
 * chapter S08–S11 and S23 write.
 *
 * ## Why this is typed TypeScript and not MDX
 *
 * `docs/www-site-plan.md` §A6 gives three reasons and the first is the only one
 * that matters: **a per-locale MDX chapter lets the structure diverge between
 * languages.** Nothing stops a Sinhala file from having six steps where the English
 * has seven, and nothing catches it — the page renders, the translation looks
 * finished, and a reader following instructions in their own language is missing
 * one. On a how-to guide for a transport platform that is a safety property, not a
 * tidiness one.
 *
 * Here the *structure* is shared and only *strings* are localised. A translator
 * replaces the text behind a key; they cannot drop a step, reorder two, or turn a
 * five-step procedure into a paragraph. A missing key is a compile error.
 *
 * The other two reasons: MDX needs `@next/mdx` plus a remark/rehype chain, and
 * every plugin is another dependency the AL-52 sweep has to reason about; and this
 * way the site renders identically with the whole backend down, which is MCS-34's
 * fourth negative.
 *
 * ## Slugs are not localised
 *
 * `/si/guide/passenger/install-and-first-run` — Sinhala content, English slug, and
 * that is deliberate. Localised slugs triple the route table, break `hreflang`
 * reciprocity (each locale would need a different path for the same document), and
 * make an external link locale-specific for no reader benefit. Recorded in
 * `portals/www/CLAUDE.md`.
 *
 * ## Every fact carries its anchor
 *
 * `Callout.source` and `Chapter.sources` are spec anchors, and README rule 7 is why:
 * a fee, a tier, a vehicle count or a "first trip free" on a public site is a
 * factual assertion about a real service. The anchor is how the next session checks
 * it is still true — and how a reader of this repo can tell a claim from a guess.
 */

import type { WwwMessageKey } from '@/i18n';
import type { GuideAudience } from '@/lib/routes';

/**
 * One instruction in a chapter.
 *
 * `instruction` and `note` are keys, never strings — this module is structure, and
 * the strings live in `src/i18n/messages/*.ts` where all three languages sit side
 * by side and the parity script can see them.
 */
export interface Step {
  readonly instruction: WwwMessageKey;
  /** An aside under the step — a caveat, a "you can skip this if…". */
  readonly note?: WwwMessageKey;
  /** A `ScreenEntry.id` from `./screens`, shown beside the step. */
  readonly screenRef?: string;
}

/**
 * What kind of aside a callout is.
 *
 * Four kinds and not a free string, because each renders differently and gets a
 * different icon and colour — and because `fee` and `privacy` are the two that
 * carry regulated claims, which is what makes {@link Callout.source} worth
 * enforcing.
 */
export type CalloutKind = 'tip' | 'warning' | 'fee' | 'privacy';

export interface Callout {
  readonly kind: CalloutKind;
  readonly body: WwwMessageKey;
  /**
   * Spec anchor. **Required whenever the callout states a fact** — which in
   * practice is every `fee` and every `privacy` callout, and any `warning` that
   * names a limit, a timeout or an amount. A `tip` that says "you can also swipe
   * down here" is describing an interaction, not asserting a fact, and may omit it.
   */
  readonly source?: string;
}

export interface Chapter {
  /** `'p01'` … `'p16'`, `'d01'` … `'d18'`. Stable, and what `relatedChapters` names. */
  readonly id: string;
  /**
   * The URL segment — `guide/passenger/<slug>`. **Never localised**, and one of
   * {@link ../content/chapters.ts}'s slug lists, so a chapter cannot invent one.
   */
  readonly slug: string;
  readonly audience: GuideAudience | 'fleet';
  /** Reading order within the audience. 1-based, matching plan §A19/§A20. */
  readonly order: number;
  readonly title: WwwMessageKey;
  readonly summary: WwwMessageKey;
  readonly steps: readonly Step[];
  readonly callouts: readonly Callout[];
  /** `ScreenEntry.id`s for the chapter's screen strip. */
  readonly screens: readonly string[];
  /**
   * A **data table** this chapter renders in full, named rather than restated.
   *
   * Added by S11 for one chapter and deliberately a one-member union rather than a
   * free string. Driver chapter 13 is the fee chapter, and its six per-vehicle
   * amounts live in exactly one place —
   * {@link ../content/marketing.ts `DAILY_FEE_TIERS`}, in minor units, with the URD
   * table named beside them and `test/content.test.ts` asserting them against it.
   * The chapter therefore states the *rule* in prose and names the *table* here, so
   * that the six numbers reach a reader without ever being typed into a message
   * string where nobody can check them.
   *
   * The alternative was for the guide page to special-case the slug
   * `the-daily-platform-fee`, which puts a magic string in a page file and breaks
   * silently the day the slug changes. This is a typed field for the same
   * information.
   */
  readonly table?: 'daily-fee-tiers';
  /** Other chapters' {@link Chapter.id}s. */
  readonly relatedChapters: readonly string[];
  /** `FaqEntry.id`s from `./faq`. */
  readonly faqRefs: readonly string[];
  /** The chapter's provenance — the spec sections it was written from. */
  readonly sources: readonly string[];
}
