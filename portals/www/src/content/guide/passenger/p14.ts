/**
 * Passenger chapter 14 — scheduling a ride.
 *
 * ## The destination is mandatory, and that is the whole of US-24.2
 *
 * AL-36 / BR-28.1 refined US-10.1 into **US-10.1a**: the schedule screen carries a
 * mandatory destination picker — the same place search and map pick as an on-demand
 * booking — plus an editable pickup defaulting to the current location, and
 * **Confirm stays disabled until a destination is set**. A disabled button is a
 * thing a reader will otherwise think is broken, so it is a step rather than a
 * detail.
 *
 * ## What happens at dispatch time is worth a passenger's while to know
 *
 * A scheduled ride is not a reservation with a named driver. It goes to the **Job
 * Board** — every future scheduled ride within 30 km, visible to standby drivers who
 * **post intent only**; there is no accept on the board (US-6A.5). **Thirty minutes
 * before the start** it is dispatched to the closest intent-poster, ranked by driver
 * level, as an ordinary offer they may still accept or reject (D1′ §B.9 step 7).
 * From there it behaves exactly like chapter 9 — including the possibility that
 * nobody accepts.
 *
 * That last point is the honest one and it is a callout: **scheduling reserves a
 * time, not a vehicle.** A guide that implies a guaranteed 5 a.m. airport run has
 * mis-sold the feature, and the Walkthrough's own edge-case row says so — *"if none
 * accept, the passenger is notified"*.
 *
 * The Job Board is described in the passenger's terms — *drivers put their hand up
 * in advance* — and the level ranking as *the more experienced of them ahead of the
 * rest*. Neither the 30 km radius nor the level numbers are published: the radius is
 * a dispatch parameter and the levels are the driver guide's (S11). What a passenger
 * is owed is why some drivers can take scheduled work and others cannot, which US-6A.8
 * settles — a Level 1 driver loses Job Board access, and that is a driver-side
 * consequence rather than a passenger-facing rule, so the copy implies it without
 * publishing the mechanism.
 *
 * **The fare is stated because it is the first thing anybody assumes wrongly.** A
 * scheduled ride uses the same Mode C tariff as any other (§1); there is no advance
 * booking premium and no discount. A guide that leaves it out invites the reader to
 * guess, and either guess is a money claim.
 *
 * ## Reminders are stated because they are the reason to use it
 *
 * **One hour and fifteen minutes before** (US-10.9), both push. The driver gets
 * their own at thirty minutes (US-6A.15) and that is S11's chapter, not this one.
 *
 * **Not published — two spec gaps the Walkthrough records under scenario 9:**
 *
 * 1. **How far ahead a ride may be scheduled is not stated anywhere.** The chapter
 *    says "a future date and time" and quotes no window. Publishing "up to a week"
 *    would be inventing a limit; publishing "any time you like" would be inventing
 *    the absence of one.
 * 2. **Whether a scheduled ride can be edited, or only cancelled and rebooked, is
 *    not stated.** `DELETE /rides/scheduled/{rideId}` exists and the Walkthrough
 *    says a passenger can *"review or cancel it later"*; nothing describes an edit.
 *    So the copy offers review and cancel, and is silent on editing rather than
 *    guessing which way it falls.
 *
 * Cancelling a scheduled ride notifies the driver with *"sufficient notice to adjust
 * plans"* (US-10.8) — which is deliberately not quoted as a number, because the story
 * does not give one.
 */

import type { Chapter } from '@/content/types';

const URD_SCHEDULING = 'specs/user-requirements-document.md#epic-24-new-2026-06-28-change-set-ux-admin-directory';
const URD_JOB_BOARD = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';

export const p14: Chapter = {
  id: 'p14',
  slug: 'scheduling-a-ride',
  audience: 'passenger',
  order: 14,
  title: 'www.guide.p14.title',
  summary: 'www.guide.p14.summary',

  steps: [
    { instruction: 'www.guide.p14.step1' },
    { instruction: 'www.guide.p14.step2', screenRef: 'SCR-PA-013' },
    { instruction: 'www.guide.p14.step3', screenRef: 'SCR-PA-013' },
    {
      instruction: 'www.guide.p14.step4',
      note: 'www.guide.p14.step4.note',
      screenRef: 'SCR-PA-013',
    },
    { instruction: 'www.guide.p14.step5', screenRef: 'SCR-PA-022' },
    { instruction: 'www.guide.p14.step6' },
    { instruction: 'www.guide.p14.step7' },
    { instruction: 'www.guide.p14.step8' },
    { instruction: 'www.guide.p14.step9', screenRef: 'SCR-PA-022' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.p14.callout.notAReservation',
      source: URD_JOB_BOARD,
    },
    {
      kind: 'tip',
      body: 'www.guide.p14.callout.reminders',
      source: 'specs/user-requirements-document.md#epic-10-notifications',
    },
  ],

  screens: ['SCR-PA-013', 'SCR-PA-022'],
  relatedChapters: ['p07', 'p09'],
  faqRefs: ['coverage', 'passenger-cost'],

  sources: [
    URD_SCHEDULING,
    URD_JOB_BOARD,
    'specs/user-requirements-document.md#epic-10-notifications',
    'specs/D5_mageride_business_logic.md#br-28-1-schedule-ride-destination-mandatory-item-2-al-36',
    'specs/D1_mageride_user_flows.md#b-9-mode-c-ride-dispatch-flow-replace',
    'specs/D5_mageride_business_logic.md#1-fare-calculation-mode-c',
    'specs/MageRide_Functional_Walkthrough.md#scenario-9-booking-a-scheduled-future-ride',
  ],
};
