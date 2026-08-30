/**
 * Passenger chapter 15 — saved places, ratings and reviews.
 *
 * Two subjects in one chapter because the plan pairs them (§A19), and they do share
 * a shape: both are things a passenger *keeps* rather than does once. The chapter
 * puts saved places first, since they change every subsequent booking.
 *
 * ## Saved places are a map pin plus three address lines, and that ordering matters
 *
 * **Home and Work are set by dropping or dragging a pin on the map**, with the
 * reverse-geocoded address shown back (US-7.13, US-22.1) — not by typing an address
 * and hoping. Any other place adds a **ModalBottomSheet capturing Address Line 1 /
 * 2 / 3 and a free-text label** of the reader's own — "Gym", "Mum's house" —
 * (US-22.2, SCR-PA-026a). All three are editable and deletable, Home and Work
 * included (US-22.3).
 *
 * They are **tied to the account, not the handset**, and are part of the eager-fetch
 * set restored on a new device (US-22.6, US-1.15). That is the sentence a reader who
 * has just changed phones wants, and it is cheap to leave out.
 *
 * The reverse geocoding is `query-svc`'s self-hosted Nominatim, as chapter 7 says.
 * The copy does not name it again; it says the address comes back from MageRide's
 * own map, which is the same claim without repeating the machinery.
 *
 * ## Ratings run both ways, and the level rule is published in the form the spec states
 *
 * A passenger rates the driver 1–5 with reason chips and an optional comment after a
 * completed Mode C **or Mode B** trip (US-18.1); the driver rates the passenger from
 * a bottom sheet on their history row (US-18.2, AL-35). Saying the second half out
 * loud is a disclosure, not a courtesy — a reader is entitled to know they are being
 * rated too.
 *
 * **Only 4★ and 5★ build a driver's level** — 5★ = 5 points, 4★ = 4, **500 points =
 * one level**, and **2★ and below count for nothing** (US-6A.6, D5 §4.2, where 3★
 * also resolves to zero). A guide that says "your rating matters" without that is
 * vague; with it, a reader understands why a driver asks. The counterweight is also
 * stated: **three passenger reports drop a level and trigger a temporary delisting**
 * (US-12.5/12.6, D5 §4.2), so a low rating and a report are different acts with
 * different consequences.
 *
 * ## The trip list, and one number that is deliberately conditional
 *
 * History is three tabs — **Past · Scheduled · Packages** — with receipts from trip
 * details (US-8.7, Walkthrough 16/17). US-24.4 puts the **driver's name and mobile
 * number with a Call action on a completed-trip card**, so a passenger can chase a
 * lost item; the number is **withheld for trips cancelled before a driver was
 * assigned**, which is the same rule chapter 10 states for the live ride and is
 * restated here because this is the screen most likely to be checked days later.
 *
 * **Not published:** the star totals, level thresholds and report counts are all
 * server-side and admin-configurable in places (US-14.12 configures the level
 * system). The chapter gives the rule as the URD states it and makes no promise that
 * the numbers are fixed for ever.
 */

import type { Chapter } from '@/content/types';

const URD_SETTINGS = 'specs/user-requirements-document.md#epic-22-new-passenger-app-settings';
const URD_RATINGS = 'specs/user-requirements-document.md#epic-18-new-ratings-reviews';
const D5_LEVELS = 'specs/D5_mageride_business_logic.md#4-2-level-rules-replace-urd-us-6a-6';

export const p15: Chapter = {
  id: 'p15',
  slug: 'saved-places-and-ratings',
  audience: 'passenger',
  order: 15,
  title: 'www.guide.p15.title',
  summary: 'www.guide.p15.summary',

  steps: [
    { instruction: 'www.guide.p15.step1', screenRef: 'SCR-PA-026' },
    { instruction: 'www.guide.p15.step2', screenRef: 'SCR-PA-026' },
    { instruction: 'www.guide.p15.step3' },
    {
      instruction: 'www.guide.p15.step4',
      note: 'www.guide.p15.step4.note',
    },
    { instruction: 'www.guide.p15.step5', screenRef: 'SCR-PA-019' },
    { instruction: 'www.guide.p15.step5b' },
    { instruction: 'www.guide.p15.step6' },
    { instruction: 'www.guide.p15.step7', screenRef: 'SCR-PA-022' },
    { instruction: 'www.guide.p15.step8' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.p15.callout.whichStarsCount',
      source: D5_LEVELS,
    },
    {
      kind: 'privacy',
      body: 'www.guide.p15.callout.ratedBothWays',
      source: URD_RATINGS,
    },
  ],

  screens: ['SCR-PA-026', 'SCR-PA-019', 'SCR-PA-022'],
  relatedChapters: ['p07', 'p11'],
  faqRefs: ['safety', 'phone-number'],

  sources: [
    URD_SETTINGS,
    URD_RATINGS,
    D5_LEVELS,
    'specs/user-requirements-document.md#epic-7-live-map-passenger-experience',
    'specs/user-requirements-document.md#epic-24-new-2026-06-28-change-set-ux-admin-directory',
    'specs/MageRide_Functional_Walkthrough.md#scenario-2-setting-up-the-profile-name-photo-saved-addresses-default-payment',
    'specs/MageRide_Functional_Walkthrough.md#scenario-16-viewing-trip-history-past-rides-scheduled-rides-packages',
    'specs/MageRide_Functional_Walkthrough.md#scenario-18-rating-a-driver-after-a-completed-trip',
  ],
};
