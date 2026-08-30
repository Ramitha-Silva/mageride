/**
 * Vision, mission and values — the three claims this site makes about what
 * MageRide *is*, as opposed to what it does.
 *
 * ## The mission is MCS-34 D1's, and only D1's
 *
 * S01 put three framings to the user — access-led, driver-livelihood-led,
 * national-infrastructure-led — and **national-infrastructure-led was chosen**:
 *
 * > *"MageRide exists to give Sri Lanka one live picture of how the country moves —
 * > every bus, every train, every three-wheeler and every van on one map, as public
 * > infrastructure rather than a private service."*
 *
 * That is the chosen *framing*. The published wording below is written from it; the
 * other two options stay in MCS-34's record and off the site.
 *
 * ## The qualifier is not optional
 *
 * D1's own decision note says the framing **carries a coverage claim that is not
 * true on launch day** — "every bus, every train" describes the design, not the
 * fleet that exists the morning the site goes live — and that S07 owes an honest
 * qualifier directly beneath the hero. {@link MISSION_QUALIFIER_KEY} is it, and
 * `test/content.test.ts` should treat it as required furniture wherever the mission
 * renders, not as decoration a layout session can drop for balance.
 *
 * This is README rule 7 at its sharpest. Every other claim on this site is checkable
 * against a spec; this one is checkable against reality, and reality will catch up
 * to it gradually. A site that says "every bus in Sri Lanka" on day one, to a reader
 * who then opens the app and sees four, has not made an ambitious claim — it has
 * made a false one.
 *
 * ## Sources
 *
 * **`MageRide_Government_Proposal.md` does not exist in this repository.** Both
 * `docs/www-site-plan.md` §0.4 and S07's own brief cite and quote it as a vision
 * source; `find` turns up no such file, and the quoted sentence appears only inside
 * those two documents. S01 flagged it and the finding holds. The real sources are
 * URD §1 and — much the better starting point, because it is already written for a
 * lay reader — `specs/MageRide_Functional_Walkthrough.md` §1.
 */

import type { WwwMessageKey } from '@/i18n';

/** A public claim and the spec that makes it true. */
export interface SourcedClaim {
  readonly body: WwwMessageKey;
  /** Spec anchor. Not optional: everything in this module is a factual assertion. */
  readonly source: string;
}

// ---------------------------------------------------------------------------
// Vision
// ---------------------------------------------------------------------------

/**
 * The hero line. One sentence, set at 40–72px in three scripts.
 *
 * Short on purpose. It has to survive Sinhala's longer word forms without wrapping
 * to four lines at 375px, which is the width A34's budget is written against — and
 * a hero that wraps to four lines in one language and two in another is a hero that
 * was designed in English.
 */
export const VISION_HERO_KEY: WwwMessageKey = 'www.vision.hero';

/** ~120 words of public copy. Rendered on `/` and in full on `/vision`. */
export const VISION_BODY_KEYS: readonly WwwMessageKey[] = [
  'www.vision.body.p1',
  'www.vision.body.p2',
  'www.vision.body.p3',
];

export const VISION_SOURCES: readonly string[] = [
  'specs/user-requirements-document.md#1-product-vision',
  'specs/MageRide_Functional_Walkthrough.md#1-platform-overview',
];

// ---------------------------------------------------------------------------
// Mission
// ---------------------------------------------------------------------------

export const MISSION_KEY: WwwMessageKey = 'www.mission.statement';

/**
 * The honest qualifier, rendered directly beneath {@link MISSION_KEY}.
 *
 * Required, per MCS-34 D1. See the module note.
 */
export const MISSION_QUALIFIER_KEY: WwwMessageKey = 'www.mission.qualifier';

export const MISSION_SOURCES: readonly string[] = [
  'build/prompts/MCS-34-www-informational-site.md#decisions-taken-d1-d10',
  'specs/user-requirements-document.md#1-product-vision',
];

// ---------------------------------------------------------------------------
// Values — six cards, each naming a real behaviour
// ---------------------------------------------------------------------------

export interface Value {
  readonly id: string;
  readonly title: WwwMessageKey;
  readonly body: WwwMessageKey;
  readonly source: string;
}

/**
 * Six values, and **not one of them is an aspiration.**
 *
 * Each names something the platform already does and carries the spec that says so.
 * "We care about drivers" is not on this list because it is not checkable; "drivers
 * keep 100% of the fare" is, and it is the same sentiment with a citation.
 *
 * The order is deliberate: the two money values first, because zero commission is
 * the single most surprising thing about MageRide to anybody who has used a
 * ride-hailing app, and the two that are most likely to be disbelieved should be the
 * two that are easiest to check.
 */
export const VALUES: readonly Value[] = [
  {
    id: 'zero-commission',
    title: 'www.values.zeroCommission.title',
    body: 'www.values.zeroCommission.body',
    source: 'specs/user-requirements-document.md#1-product-vision',
  },
  {
    id: 'passengers-pay-nothing',
    title: 'www.values.passengersFree.title',
    body: 'www.values.passengersFree.body',
    source: 'specs/user-requirements-document.md#daily-platform-fee-structure-namma-yatri-methodology',
  },
  {
    id: 'first-trip-free',
    title: 'www.values.firstTripFree.title',
    body: 'www.values.firstTripFree.body',
    source: 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing',
  },
  {
    id: 'trilingual',
    title: 'www.values.trilingual.title',
    body: 'www.values.trilingual.body',
    source: 'CLAUDE.md#universal-rules',
  },
  {
    id: 'open-mapping',
    title: 'www.values.openMapping.title',
    body: 'www.values.openMapping.body',
    source: 'specs/D6_mageride_integration.md#7-6-maps-tiles-pmtiles-nominatim-d-14',
  },
  {
    id: 'your-data',
    title: 'www.values.yourData.title',
    body: 'www.values.yourData.body',
    source: 'specs/architecture-design-document.md#pdpa-svc',
  },
];
