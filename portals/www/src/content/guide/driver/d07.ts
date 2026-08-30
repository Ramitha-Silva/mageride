/**
 * Driver chapter 7 — going on standby and staying visible.
 *
 * The toggle is one tap and everything interesting about it is a precondition, so the
 * chapter is mostly "why the switch is grey".
 *
 * ## The gate (US-2.25 / US-9.6)
 *
 * **The Go Online toggle is disabled until at least one vehicle is available** — an
 * owned Mode C vehicle that is **Approved** in My Vehicles, or a **shared or
 * temporarily assigned** Mode A/B vehicle. Approval is chapter 4's; assignment comes
 * from a fleet and needs no wizard at all. With neither, the screen says so and offers
 * the onboarding popup.
 *
 * ## The money precondition, stated exactly and no further
 *
 * **The first trip of every day is free.** The daily platform fee is deducted from
 * the wallet **before the second trip**, it is a single flat charge for the day
 * however many trips follow, and **there is no charge on a day the driver does not go
 * online** (US-9.1, US-9.4). If the balance cannot cover it the request is **missed**
 * and the driver is told why — which is the sharpest version of this chapter's point:
 * an empty wallet does not show an error, it shows nothing at all, and a driver who
 * does not know that will think dispatch is broken.
 *
 * **Rs 200 is the platform default low-balance threshold and the driver can set their
 * own** (US-9.9). Both halves matter; only quoting the default would make a
 * configurable alert sound fixed.
 *
 * The six per-vehicle amounts are the daily-fee chapter's, with `DAILY_FEE_TIERS` and
 * the test that holds them to the URD. This chapter names the rule, not the table.
 *
 * ## "Staying visible" is three separate rules and they are easy to confuse
 *
 * - **One vehicle publishes at a time** (US-3.6, US-9.6): selecting a vehicle in My
 *   Vehicles makes it the single active publisher.
 * - **Going offline clears an active Directional Travel filter** (US-6A.19), which is
 *   the trap — the filter costs a daily use and going offline throws it away.
 *   Directional Travel has its own chapter and this is only the interaction.
 * - **A vehicle that stops sending positions is removed from the map** until it
 *   resumes (US-7.17), and **a Mode C vehicle already on a hire is not on the public
 *   map at all** (US-7.16). Neither is a fault, and both look like one.
 */

import type { Chapter } from '@/content/types';

const URD_DISPATCH = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';
const URD_VEHICLES = 'specs/user-requirements-document.md#epic-2-vehicle-registration';

export const d07: Chapter = {
  id: 'd07',
  slug: 'going-on-standby',
  audience: 'driver',
  order: 7,
  title: 'www.guide.d07.title',
  summary: 'www.guide.d07.summary',

  steps: [
    { instruction: 'www.guide.d07.step1', screenRef: 'SCR-DA-010' },
    { instruction: 'www.guide.d07.step2', note: 'www.guide.d07.step2.note' },
    { instruction: 'www.guide.d07.step3', screenRef: 'SCR-DA-010' },
    { instruction: 'www.guide.d07.step4' },
    { instruction: 'www.guide.d07.step5' },
    { instruction: 'www.guide.d07.step6' },
    { instruction: 'www.guide.d07.step7' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.d07.callout.firstTripFree',
      source: URD_FEES,
    },
    {
      kind: 'warning',
      body: 'www.guide.d07.callout.lowBalance',
      source: URD_FEES,
    },
    {
      kind: 'tip',
      body: 'www.guide.d07.callout.oneVehicleLive',
      source: URD_VEHICLES,
    },
  ],

  screens: ['SCR-DA-010'],
  relatedChapters: ['d06', 'd08', 'd04', 'd10', 'd13'],
  faqRefs: ['daily-fee', 'fee-off-days', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-24-going-online-standby-toggle-and-waiting-for-a-ride-request',
    URD_DISPATCH,
    URD_FEES,
    URD_VEHICLES,
    'specs/user-requirements-document.md#epic-7-live-map-passenger-experience',
    'specs/D1_mageride_user_flows.md#b-8-go-online-session-flow-new-mode-a-replace-mode-c-standby',
  ],
};
