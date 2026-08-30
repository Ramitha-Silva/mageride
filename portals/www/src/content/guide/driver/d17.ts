/**
 * Driver chapter 17 — ratings, driver level, and what they change.
 *
 * URD Epic 18 (ratings), Epic 6A's Driver Level system (US-6A.6/6A.7/6A.8/6A.14),
 * D5 §3.3 (the dispatch score), reputation-svc, and Walkthrough scenario 36.
 * Screen SCR-DA-019.
 *
 * ## The dispatch question, answered from the specs and not around them
 *
 * S11's fence says: *only describe an effect on dispatch if the specs state one; if
 * they do not, say ratings are visible and leave it there.* **They do state one, in
 * three places**, so the chapter states exactly those three and stops:
 *
 * - **US-6A.2** — a request goes to the nearest available driver *"based on
 *   **distance**, **Driver Rating/Level**, and **vehicle category**"*.
 * - **D5 §3.3** — the score is `w_dist · f(distance) + w_level · (driverLevel / 3) +
 *   w_cat · categoryMatch`, and **the weights are versioned and admin-configurable**.
 * - **US-6A.5** — for a scheduled ride, where two intent-posters are in nearby
 *   cells, **the higher-level driver is rung first**.
 *
 * What the chapter does **not** say is how much a level is worth, because the weights
 * are configuration and nothing publishes them. "Level is one of three things that
 * decide who gets rung, alongside how close you are and what you drive" is the whole
 * of the claim, and it is checkable.
 *
 * ## Level 1 is a lock, not a ban, and the difference is the driver's livelihood
 *
 * US-6A.8 is explicit and its own parenthesis does the work: a Level 1 driver loses
 * the **Job Board and scheduled rides** and *"can still operate in other modes"*.
 * Written as a ban it would read as an account suspension. Written as a lock with a
 * way back — the same points ladder everyone else climbs — it is accurate and it is
 * actionable.
 *
 * ## The arithmetic, exactly as US-6A.6 gives it
 *
 * Everyone **starts at Level 3**. Points come from ratings: a 5-star is 5 points, a
 * 4-star is 4, and **2 stars or below count nothing**; **500 points is one level**
 * (*"every 100 five-star ratings = 500 points = 1 level up"*, with the URD's own
 * mixed example, 50×5★ + 65×4★ = 510). Two things take a level away and there are
 * only two: **failing to appear for a scheduled ride you accepted** (US-6A.7) and
 * **three passenger reports**, which also triggers a temporary delisting (US-6A.6,
 * US-12.6). Chapters 8 and 9 already state both from the other direction; this is
 * where they are collected.
 *
 * ## Acceptance rate is shown, and nothing published says what it does
 *
 * US-6A.14 puts acceptance rate and no-show history on this screen. **No spec
 * attaches a consequence to a low acceptance rate** — chapter 8 reached the same
 * finding about a missed offer and recorded it, and this chapter says the same thing
 * rather than quietly implying a threshold. A guide that hints at an unstated penalty
 * is inventing pressure, which is both a false claim and a bad incentive.
 *
 * ## Rating the passenger back
 *
 * US-18.2: after a completed Mode C trip the driver may rate the passenger 1–5 with
 * an optional comment, and it opens **as a bottom sheet from the ride-history row**
 * (2026-06-25 item 13) rather than as an inline card. Named precisely because a
 * driver who expects a prompt at drop-off will conclude the feature is missing.
 */

import type { Chapter } from '@/content/types';

const URD_RATINGS = 'specs/user-requirements-document.md#epic-18-new-ratings-reviews';
const URD_DISPATCH =
  'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';
const D5_DISPATCH =
  'specs/D5_mageride_business_logic.md#3-dispatch-algorithm-mode-c-adapt-ny-allocator-driverpool-dispatch-svc';

export const d17: Chapter = {
  id: 'd17',
  slug: 'ratings-and-driver-level',
  audience: 'driver',
  order: 17,
  title: 'www.guide.d17.title',
  summary: 'www.guide.d17.summary',

  steps: [
    { instruction: 'www.guide.d17.step1' },
    { instruction: 'www.guide.d17.step2', note: 'www.guide.d17.step2.note' },
    { instruction: 'www.guide.d17.step3', screenRef: 'SCR-DA-019' },
    { instruction: 'www.guide.d17.step4', screenRef: 'SCR-DA-019' },
    { instruction: 'www.guide.d17.step5' },
    { instruction: 'www.guide.d17.step6', note: 'www.guide.d17.step6.note' },
    { instruction: 'www.guide.d17.step7', screenRef: 'SCR-DA-019' },
    { instruction: 'www.guide.d17.step8' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d17.callout.whatLevelChanges',
      source: URD_DISPATCH,
    },
    {
      kind: 'warning',
      body: 'www.guide.d17.callout.twoWaysToDrop',
      source: URD_DISPATCH,
    },
    {
      kind: 'tip',
      body: 'www.guide.d17.callout.acceptanceRate',
      source: URD_DISPATCH,
    },
  ],

  screens: ['SCR-DA-019'],
  relatedChapters: ['d08', 'd09', 'd18'],
  faqRefs: ['safety', 'become-a-driver'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-36-the-driver-level-system-level-1-restrictions-and-progressing-to-level-2',
    URD_RATINGS,
    URD_DISPATCH,
    'specs/user-requirements-document.md#epic-12-safety-trust',
    D5_DISPATCH,
    'specs/D2_mageride_ui_spec.md#scr-da-019-scr-di-019-driver_level-driver-level-stats-new-us-6a-6-6a-14',
    'specs/architecture-design-document.md#6-service-catalogue-reputation-svc',
  ],
};
