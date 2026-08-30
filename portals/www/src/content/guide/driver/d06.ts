/**
 * Driver chapter 6 — your dashboard.
 *
 * ## The dashboard is not one screen, and that is the chapter
 *
 * **When the active vehicle is Mode A or Mode B, the Start/End-Journey screen IS the
 * home dashboard** (US-5.11) — not a screen reached from it. SCR-DA-011 replaces
 * SCR-DA-010 entirely, carries only **Start Journey** and **End Journey**, and shows
 * the **vehicle type and number below the route card**. A driver switching between an
 * own three-wheeler and an assigned school van sees two different home screens, and
 * a guide that described only the standby map would leave half its readers looking
 * for a toggle that is not there.
 *
 * ## Two facts about SCR-DA-010 that read as omissions unless stated (US-7.18)
 *
 * - **The map shows only the driver's own active vehicle.** Other drivers are never
 *   drawn there. A driver who expects a competitor heat-map will read an empty map
 *   as a broken one.
 * - **There is no top-left hamburger.** Navigation is the bottom **Menu** tab. This
 *   is the single most likely "where is it" question the screen produces, and it is
 *   one sentence to answer.
 *
 * ## The GPS-device banner (US-5.12)
 *
 * A paired tracker starts the journey on **ignition on** and stops publishing on
 * ignition off, so a Mode A driver can open the app and find the journey already
 * started. The dashboard **overrides the device in both directions** — the driver may
 * Start or End manually whatever the tracker is doing. Stated because the alternative
 * reading (the device is in charge) is the one a driver would otherwise reach.
 *
 * ## Money on this screen, and where the numbers live
 *
 * The header carries a level badge, a rating, the wallet balance and a **daily-fee
 * chip** (US-9.7). The chapter names them and stops: **Mode A pays no daily fee and
 * Mode B is a monthly charge**, both anchored, and the six Mode C tier amounts belong
 * to the daily-fee chapter, which owns `DAILY_FEE_TIERS` and the test that asserts it
 * against the URD. Quoting six amounts in two chapters is two places to get them
 * wrong.
 *
 * **Mode B's figure is "about Rs 300 a month"**, following `src/content/marketing.ts`:
 * the URD says *approximately* in both places it states it, and an approximate price
 * rendered as a precise one is a false claim with a decimal point on it.
 */

import type { Chapter } from '@/content/types';

const URD_SESSIONS = 'specs/user-requirements-document.md#epic-5-driver-session-management-bus-mode-a';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';
const URD_MAP = 'specs/user-requirements-document.md#epic-7-live-map-passenger-experience';

export const d06: Chapter = {
  id: 'd06',
  slug: 'your-dashboard',
  audience: 'driver',
  order: 6,
  title: 'www.guide.d06.title',
  summary: 'www.guide.d06.summary',

  steps: [
    { instruction: 'www.guide.d06.step1', screenRef: 'SCR-DA-010' },
    { instruction: 'www.guide.d06.step2', screenRef: 'SCR-DA-010' },
    {
      instruction: 'www.guide.d06.step3',
      note: 'www.guide.d06.step3.note',
      screenRef: 'SCR-DA-010',
    },
    { instruction: 'www.guide.d06.step4', screenRef: 'SCR-DA-010' },
    { instruction: 'www.guide.d06.step5', screenRef: 'SCR-DA-011' },
    { instruction: 'www.guide.d06.step6', screenRef: 'SCR-DA-011' },
    {
      instruction: 'www.guide.d06.step7',
      note: 'www.guide.d06.step7.note',
      screenRef: 'SCR-DA-011',
    },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d06.callout.whichHomeScreen',
      source: URD_SESSIONS,
    },
    {
      kind: 'fee',
      body: 'www.guide.d06.callout.whoPaysWhat',
      source: URD_FEES,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d06.callout.ownVehicleOnly',
      source: URD_MAP,
    },
  ],

  screens: ['SCR-DA-010', 'SCR-DA-011'],
  relatedChapters: ['d07', 'd05', 'd13'],
  faqRefs: ['modes', 'daily-fee', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-24-going-online-standby-toggle-and-waiting-for-a-ride-request',
    'specs/D1_mageride_user_flows.md#b-8-go-online-session-flow-new-mode-a-replace-mode-c-standby',
    URD_SESSIONS,
    URD_MAP,
    URD_FEES,
    'specs/D2_mageride_ui_spec.md#scr-da-010-scr-di-010-dashboard-driver-dashboard-home-primary-replace-map-mode-aware',
    'specs/D2_mageride_ui_spec.md#scr-da-011-scr-di-011-mode_a_session-start-end-journey-mode-a-new-us-5-1-5-6',
  ],
};
