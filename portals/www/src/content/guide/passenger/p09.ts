/**
 * Passenger chapter 9 — waiting, and what the 15-second dispatch is doing.
 *
 * ## Two numbers, both public, both anchored
 *
 * The **15 seconds** is the offer TTL held against **one** driver (D1′ §B.9 step 2,
 * R-07; US-6A.2/6A.3). The **2 minutes** is the passenger-side matching timeout,
 * after which the request auto-cancels and the app offers a retry (US-6A.11). They
 * are easy to conflate into "you wait fifteen seconds", which is wrong and reads as
 * a promise the platform never made — so the chapter states them as what they are:
 * one is how long each driver has, the other is how long the search runs.
 *
 * ## The dispatch is a cascade, not an auction, and saying so is the point
 *
 * D1′ §B.9 offers to a **single reserved top candidate** (Redis Lua + a Postgres
 * unique partial index, R-10), and on decline or timeout re-offers to the next. A
 * reader who has used a bidding app will assume drivers are competing on price;
 * they are not, because the fare is already fixed by chapter 8's tariff. Describing
 * the mechanism plainly is what stops that assumption forming.
 *
 * The scoring inputs — distance, driver level, vehicle category (US-6A.2, R-11) —
 * are named without the weights, which are a versioned server-side score and not a
 * public claim.
 *
 * **Directional Travel is described from the passenger's side and only there.** A
 * standby driver may set a destination filter for a limited period and a limited
 * number of times a day (US-6A.17, DT-01…DT-08), after which they are only offered
 * hires moving them towards it. A passenger cannot see it and cannot do anything
 * about it — but it is a real reason a car that is visibly nearby never gets their
 * ride, and saying so is kinder than letting them conclude the app is broken. The
 * angles, the 2 km detour limit and the daily-use count are the driver's chapter's
 * (S11) and are not published here.
 *
 * The ADD's `dispatch-svc` route list annotates `POST …/offer/{driverId}/reject` as
 * carrying **no penalty**, which is worth one sentence for the same reason: a reader
 * watching three drivers pass on their ride should not read it as a judgement of
 * them. The same list is where the free pre-acceptance cancel is spelled out as a
 * terminal state — `CancelledByRiderBeforeAccept (terminal, no penalty)` — which
 * corroborates US-6A.9 from the other side of the system.
 *
 * ## Cancellation is split across two chapters on purpose
 *
 * **Free before a driver accepts** (US-6A.9) belongs here, because "Cancel (free)"
 * is drawn on SCR-PA-014 itself. The **Rs 50 after acceptance** and the
 * three-in-a-row booking block (US-6A.10/6A.10b) belong to chapter 10, because that
 * is the screen they happen on — and because the figure is then stated once on the
 * site rather than twice in two chapters that can drift apart.
 */

import type { Chapter } from '@/content/types';

const D1_DISPATCH = 'specs/D1_mageride_user_flows.md#b-9-mode-c-ride-dispatch-flow-replace';
const URD_DISPATCH = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';

export const p09: Chapter = {
  id: 'p09',
  slug: 'waiting-for-a-driver',
  audience: 'passenger',
  order: 9,
  title: 'www.guide.p09.title',
  summary: 'www.guide.p09.summary',

  steps: [
    { instruction: 'www.guide.p09.step1', screenRef: 'SCR-PA-014' },
    { instruction: 'www.guide.p09.step2' },
    { instruction: 'www.guide.p09.step3' },
    { instruction: 'www.guide.p09.step4' },
    {
      instruction: 'www.guide.p09.step5',
      note: 'www.guide.p09.step5.note',
      screenRef: 'SCR-PA-014',
    },
    { instruction: 'www.guide.p09.step6' },
    { instruction: 'www.guide.p09.step7' },
    { instruction: 'www.guide.p09.step8', screenRef: 'SCR-PA-014' },
    { instruction: 'www.guide.p09.step9' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.p09.callout.twoMinutes',
      source: URD_DISPATCH,
    },
    {
      kind: 'fee',
      body: 'www.guide.p09.callout.cancelFree',
      source: URD_DISPATCH,
    },
  ],

  screens: ['SCR-PA-014'],
  relatedChapters: ['p08', 'p10'],
  faqRefs: ['coverage', 'passenger-cost'],

  sources: [
    D1_DISPATCH,
    URD_DISPATCH,
    'specs/D5_mageride_business_logic.md#12-directional-travel-new-dt-01-dt-08-pickme-style',
    'specs/architecture-design-document.md#dispatch-svc-mode-c-on-demand-standby',
    'specs/D5_mageride_business_logic.md#3-5-offer-ttl-cascade-adapt-ny-batch-popupdelay-15-s-redis-quartz-backstop',
    'specs/MageRide_Functional_Walkthrough.md#scenario-4-booking-a-regular-on-demand-ride-tuk-sedan-van-for-yourself',
  ],
};
