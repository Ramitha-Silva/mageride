/**
 * Driver chapter 10 — directional travel.
 *
 * The destination filter, from US-6A.17–6A.23, D5 §12 (DT-01..DT-08), Walkthrough
 * scenario 32 and the SCR-DA-013 frame.
 *
 * ## Every number in this chapter is a default, and saying so is the chapter
 *
 * **Two uses a day, two hours each, a two-kilometre pickup detour** — and all three
 * are `admin-configurable` in the URD's own word (US-6A.18, US-6A.17). A guide that
 * prints them as rules has told a driver that a setting is a law, and the day
 * MageRide raises the daily limit the public site is wrong in the direction that
 * costs the reader something. So each is written as *"today it is set to"*, which is
 * both true and stable.
 *
 * D5 §12.1's third condition carries a fourth default (`progress_min`, 250 m) and it
 * is **left out on purpose**: it is a server-side predicate a driver cannot observe,
 * and quoting it would invite the reading that a drop-off 200 m closer is somehow
 * the driver's fault. The chapter states the shape of the rule — *the trip has to
 * leave you closer than it found you* — and not its tolerance.
 *
 * ## The two traps, which are the reason this chapter is worth reading
 *
 * - **Turning it off early still spends the use** (US-6A.19). That is stated in the
 *   specs as an anti-gaming measure and it is the single most expensive surprise on
 *   this screen: a driver who sets it, changes their mind in five minutes and turns
 *   it off has spent half of that day's allowance for nothing.
 * - **Going offline clears it** (US-6A.19), and clearing it that way is the same
 *   spent use. Chapter 7 states the same fact from the standby side; it is repeated
 *   here because this is the chapter a driver reads *before* setting one.
 *
 * ## What it does not do, stated as flatly as what it does
 *
 * It **narrows** an eligible pool and never widens one — the predicate "only removes
 * otherwise-eligible candidates, never adds ineligible, never relaxes a gate"
 * (DT-05). It does not change the fare, the zero-commission model or the daily fee
 * (the URD's own summary paragraph says so in that order). And **no directional
 * hires arriving is not a penalty**: US-6A.23 is explicit that there is no Driver
 * Level and no acceptance-rate impact, which matters because the visible effect of
 * the filter is a quiet phone, and a quiet phone is what an acceptance-rate penalty
 * would also look like.
 */

import type { Chapter } from '@/content/types';

const URD_DISPATCH =
  'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';
const D5_DIRECTIONAL =
  'specs/D5_mageride_business_logic.md#12-directional-travel-new-dt-01-dt-08-pickme-style';

export const d10: Chapter = {
  id: 'd10',
  slug: 'directional-travel',
  audience: 'driver',
  order: 10,
  title: 'www.guide.d10.title',
  summary: 'www.guide.d10.summary',

  steps: [
    { instruction: 'www.guide.d10.step1', screenRef: 'SCR-DA-013' },
    { instruction: 'www.guide.d10.step2', screenRef: 'SCR-DA-013' },
    {
      instruction: 'www.guide.d10.step3',
      note: 'www.guide.d10.step3.note',
      screenRef: 'SCR-DA-013',
    },
    { instruction: 'www.guide.d10.step4', screenRef: 'SCR-DA-013' },
    { instruction: 'www.guide.d10.step5' },
    { instruction: 'www.guide.d10.step6', note: 'www.guide.d10.step6.note' },
    { instruction: 'www.guide.d10.step7' },
    { instruction: 'www.guide.d10.step8' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d10.callout.turningItOffCosts',
      source: URD_DISPATCH,
    },
    {
      kind: 'tip',
      body: 'www.guide.d10.callout.narrowsNeverWidens',
      source: D5_DIRECTIONAL,
    },
    {
      kind: 'tip',
      body: 'www.guide.d10.callout.noPenalty',
      source: URD_DISPATCH,
    },
  ],

  screens: ['SCR-DA-013'],
  relatedChapters: ['d07', 'd08', 'd11'],
  faqRefs: ['become-a-driver', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-32-using-the-directional-travel-filter-destination-and-daily-limit',
    URD_DISPATCH,
    D5_DIRECTIONAL,
    'specs/D2_mageride_ui_spec.md#scr-da-013-scr-di-013-directional_travel-directional-travel-filter-new-us-6a-17-23-dt-08',
    'specs/user-requirements-document.md#epic-10-notifications',
  ],
};
