/**
 * Passenger chapter 4 — tracking a bus or a train (Mode A).
 *
 * Mode A belongs to `trip-state-svc` and the public routes come from `transit-svc`'s
 * GTFS dataset; neither is `ride-svc`, and the chapter never lets the two blur. There
 * is no Book button on a public route and no fare of any kind — the Walkthrough says
 * it in one line ("MageRide only shows position and ETA; there is never a fare or a
 * Book button, only Track Route") and that line is the shape of the whole chapter.
 *
 * ## The feed is a dependency and the chapter says so
 *
 * Route information is an **externally provided GTFS file** loaded by an administrator
 * (URD Epic 28; ADD AL-18/AL-54/AL-55/AL-56 — there is no in-house authoring). A route
 * that is not in the feed cannot be listed no matter how many buses run it. Publishing
 * "every bus route in Sri Lanka" would be the same false claim the mission qualifier
 * exists to prevent, one layer down, so {@link callouts} states the dependency plainly.
 *
 * **Known gap, not published:** the Walkthrough flags that the specs do not describe
 * what a passenger sees for a route that is in the feed but has no vehicle currently
 * reporting (timetable-only fallback vs nothing). The chapter says the route draws and
 * no vehicle appears on it, which is what the map rules give (US-7.17 removes a
 * non-reporting vehicle); it promises no timetable.
 */

import type { Chapter } from '@/content/types';

export const p04: Chapter = {
  id: 'p04',
  slug: 'tracking-buses-and-trains',
  audience: 'passenger',
  order: 4,
  title: 'www.guide.p04.title',
  summary: 'www.guide.p04.summary',

  steps: [
    { instruction: 'www.guide.p04.step1', screenRef: 'SCR-PA-007' },
    { instruction: 'www.guide.p04.step2', screenRef: 'SCR-PA-008' },
    { instruction: 'www.guide.p04.step3', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p04.step4', screenRef: 'SCR-PA-009' },
    { instruction: 'www.guide.p04.step5' },
    { instruction: 'www.guide.p04.step6' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.p04.callout.free',
      source: 'specs/user-requirements-document.md#epic-9',
    },
    {
      kind: 'warning',
      body: 'www.guide.p04.callout.gtfs',
      source: 'specs/user-requirements-document.md#epic-28',
    },
    {
      // Admin-only train registration is `registry-svc`'s rule, and it is the reason
      // a driver reading the driver guide will not find a "register a train" step.
      kind: 'tip',
      body: 'www.guide.p04.callout.trains',
      source: 'specs/architecture-design-document.md#6-service-catalogue-registry-svc',
    },
  ],

  screens: ['SCR-PA-007', 'SCR-PA-009'],
  relatedChapters: ['p03', 'p07'],
  faqRefs: ['trains', 'coverage', 'modes'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-11-tracking-a-public-bus-or-train-mode-a-on-the-live-map',
    'specs/user-requirements-document.md#epic-7',
    'specs/user-requirements-document.md#epic-8',
    'specs/user-requirements-document.md#epic-28',
    'specs/D1_mageride_user_flows.md#f-23-1-book-a-public-transport-trip-geo-only-gtfs',
  ],
};
