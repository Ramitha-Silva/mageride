/**
 * Passenger chapter 3 — reading the live map.
 *
 * ## Ten vehicle types, eleven colours, and why the chapter says both
 *
 * S08's brief asks this chapter to "name all eleven vehicle types". There are **ten**.
 * The eleventh token in the canonical palette — D2 §0.2's `vehPrivate`, grey — is a
 * **Mode B display colour**: a private vehicle is a sedan or a van drawn grey because
 * of how it is shared, not a kind of vehicle anybody can book. `tokens.ts` says so in
 * its own comment, URD §1.B enumerates ten, and S07 already made the same correction
 * for the stats band (`STATS.vehicle-types = 10`, `marketing.ts`).
 *
 * Naming eleven types would invent one a reader could then go looking for, so the
 * chapter names the ten and then explains the grey — which is more useful anyway,
 * because grey is the colour a reader is most likely to tap and be surprised by
 * (US-4.9: a Mode B marker opens the access request, never the detail popup).
 *
 * The **mode badges** are a third set again (green / grey / orange, D2 §0.2), and the
 * near-collision of two greys and two greens is exactly why {@link callouts} spends a
 * callout separating them.
 */

import type { Chapter } from '@/content/types';

const URD_MAP = 'specs/user-requirements-document.md#epic-7';

export const p03: Chapter = {
  id: 'p03',
  slug: 'reading-the-live-map',
  audience: 'passenger',
  order: 3,
  title: 'www.guide.p03.title',
  summary: 'www.guide.p03.summary',

  steps: [
    { instruction: 'www.guide.p03.step1', screenRef: 'SCR-PA-010' },
    { instruction: 'www.guide.p03.step2', screenRef: 'SCR-PA-010' },
    { instruction: 'www.guide.p03.step3', screenRef: 'SCR-PA-007' },
    { instruction: 'www.guide.p03.step4' },
    { instruction: 'www.guide.p03.step5' },
    { instruction: 'www.guide.p03.step6', screenRef: 'SCR-PA-006' },
    { instruction: 'www.guide.p03.step7' },
    { instruction: 'www.guide.p03.step8' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.p03.callout.modeBadges',
      source: 'specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens',
    },
    {
      // The coverage sentence is the mission qualifier restated where a reader meets
      // the evidence for it: an empty map. `vision.ts` owns the claim; this is the
      // same honesty applied to the screen it shows up on.
      kind: 'tip',
      body: 'www.guide.p03.callout.coverage',
      source: URD_MAP,
    },
  ],

  screens: ['SCR-PA-010', 'SCR-PA-006', 'SCR-PA-007'],
  relatedChapters: ['p04', 'p05'],
  faqRefs: ['vehicle-types', 'modes', 'coverage'],

  sources: [
    URD_MAP,
    'specs/user-requirements-document.md#1-b-canonical-vehicle-types',
    'specs/D2_mageride_ui_spec.md#0-2-mageride-design-tokens',
    'specs/D2_mageride_ui_spec.md#0-3-map-marker-patterns',
    'specs/MageRide_Functional_Walkthrough.md#scenario-3-finding-nearby-vehicles-on-the-live-map-and-filtering-by-type',
  ],
};
