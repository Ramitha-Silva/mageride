/**
 * Passenger chapter 1 — install and first run.
 *
 * Written from Walkthrough Section A scenario 1, which is already a step table for
 * a lay reader; the work here was selection and rewriting, not research.
 *
 * ## Two things the brief asked for that are not in the app, and are therefore not here
 *
 * **The passenger does not choose a city.** S08's brief titles this chapter
 * "language, city, phone + OTP, profile". US-1.3a is written "as a user", but the
 * screen that carries the city list is the **driver's** (SCR-DA-002 · "language and
 * city"); the passenger's SCR-PA-002 has three onboarding slides and the language
 * boxes and nothing else, in D2, in D1′ A.1 and in the approved wireframe. A step
 * telling a reader to pick their city would be a control the screen beside it does
 * not have, which is the fence this chapter is most likely to break.
 *
 * **The apps are not published**, so this chapter cannot open with "install it from
 * the store". {@link callouts} says so in the reader's own words rather than letting
 * the guide imply a download that does not exist (MCS-34 D3, the same decision
 * `/download` renders).
 */

import type { Chapter } from '@/content/types';

export const p01: Chapter = {
  id: 'p01',
  slug: 'install-and-first-run',
  audience: 'passenger',
  order: 1,
  title: 'www.guide.p01.title',
  summary: 'www.guide.p01.summary',

  steps: [
    { instruction: 'www.guide.p01.step1', screenRef: 'SCR-PA-002' },
    { instruction: 'www.guide.p01.step2', screenRef: 'SCR-PA-003' },
    {
      instruction: 'www.guide.p01.step3',
      note: 'www.guide.p01.step3.note',
      screenRef: 'SCR-PA-003',
    },
    { instruction: 'www.guide.p01.step4', screenRef: 'SCR-PA-004' },
    { instruction: 'www.guide.p01.step5' },
    { instruction: 'www.guide.p01.step6' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.p01.callout.notPublished',
      source: 'build/prompts/MCS-34-www-informational-site.md#decisions-taken-d1-d10',
    },
    {
      // AL-07's scope, said plainly: a reader who has used other apps expects a
      // "continue with Google" button and its absence is worth explaining rather
      // than leaving them hunting for it.
      kind: 'privacy',
      body: 'www.guide.p01.callout.phoneOnly',
      source: 'specs/user-requirements-document.md#epic-1',
    },
    {
      // US-1.12/1.13. A warning rather than a tip: somebody who signs in on a second
      // phone mid-journey loses the first one, and finding that out from the guide is
      // cheaper than finding it out at a junction.
      kind: 'warning',
      body: 'www.guide.p01.callout.oneDevice',
      source: 'specs/user-requirements-document.md#epic-1',
    },
  ],

  screens: ['SCR-PA-002', 'SCR-PA-003', 'SCR-PA-004'],
  relatedChapters: ['p02', 'p07'],
  faqRefs: ['signup', 'languages'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-1-new-passenger-signs-up-and-verifies-their-phone-number',
    'specs/user-requirements-document.md#epic-1',
    'specs/D1_mageride_user_flows.md#a-1-screen-inventory',
    'specs/D2_mageride_ui_spec.md#scr-pa-002-onboarding-carousel-language',
  ],
};
