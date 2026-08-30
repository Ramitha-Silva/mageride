/**
 * Driver chapter 2 — onboarding your vehicle.
 *
 * ## Three doors, three meanings — this is the chapter MCS-06 was raised for
 *
 * `build/prompts/MCS-06-plus-starts-a-new-vehicle.md` (2026-08-21) split what had
 * been one behaviour into three, and the guide has to say which is which because the
 * confusion it fixed was a driver being dropped onto *"Step 2 of 4 · Insurance"* for
 * a vehicle they had not chosen:
 *
 * | Door | What it does now |
 * |---|---|
 * | **＋** in My Vehicles | a fresh **Step 1/4 for a new vehicle**, unconditionally — ＋ means add |
 * | **Resume ›** on a row | continues **that** vehicle at its own next incomplete step |
 * | **Vehicle Onboarding** in the nav drawer | names no vehicle, so it resumes the first incomplete one — deliberately unchanged (AL-30) |
 *
 * **US-2.27 has been rewritten and the old wording is in older specs.** Anything
 * that says ＋ resumes an unfinished vehicle is superseded; the wireframe's own
 * `.states` prose still carries it, and the drawn frame — ＋ in the header, *Resume ›*
 * on the Incomplete row — is the correct one. The frame is what this chapter shows.
 *
 * ## SCR-DA-026 was added to the registry by this session
 *
 * S05 curated 69 of 202 frames and My Vehicles was not among them; it is the one
 * screen that draws all three doors at once, so per S08's rule — *the guide is the
 * customer of the registry* — it was added here and the capture re-run for it. The
 * registry now holds 70. (S05's prose said 68; the passenger group has always held
 * 29 rather than the 28 its comment claimed, and both totals were corrected in the
 * same change.)
 *
 * ## What this wizard is not for
 *
 * **Mode C only.** US-2.24 removed the vehicle registration document, the permit and
 * the GPS-tracker field from driver-app onboarding: a bus, a school van and their
 * route permits are the Fleet Portal's business (SCR-FP-004, US-27.3). A driver who
 * reads this chapter and goes looking for a permit slot will not find one, and the
 * copy says so rather than letting them hunt.
 *
 * **Known discrepancy, not published either way.** SCR-DA-004's hint line prints the
 * full canonical ten (AL-09) including bus and train, while D2's own component table
 * for the same screen says *"Mode C; no bus/train"*. The chapter names the Mode C
 * types from **US-2.3**, which agrees with D2 and with what the wizard is for.
 */

import type { Chapter } from '@/content/types';

const URD_VEHICLES = 'specs/user-requirements-document.md#epic-2-vehicle-registration';
const MCS_06 = 'build/prompts/MCS-06-plus-starts-a-new-vehicle.md#the-decision';

export const d02: Chapter = {
  id: 'd02',
  slug: 'onboarding-your-vehicle',
  audience: 'driver',
  order: 2,
  title: 'www.guide.d02.title',
  summary: 'www.guide.d02.summary',

  steps: [
    { instruction: 'www.guide.d02.step1', note: 'www.guide.d02.step1.note' },
    { instruction: 'www.guide.d02.step2', screenRef: 'SCR-DA-026' },
    { instruction: 'www.guide.d02.step3', screenRef: 'SCR-DA-004' },
    { instruction: 'www.guide.d02.step4', screenRef: 'SCR-DA-004a' },
    { instruction: 'www.guide.d02.step5' },
    { instruction: 'www.guide.d02.step6', screenRef: 'SCR-DA-026' },
    {
      instruction: 'www.guide.d02.step7',
      note: 'www.guide.d02.step7.note',
      screenRef: 'SCR-DA-026',
    },
    { instruction: 'www.guide.d02.step8' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d02.callout.threeDoors',
      source: MCS_06,
    },
    {
      // US-2.7 + D-37. Two different uniqueness rules that both surface as the same
      // error message, and a driver whose plate is already registered to a phone
      // they no longer use needs to know that support is the route, not a retry.
      kind: 'warning',
      body: 'www.guide.d02.callout.oneVehicleOnePhone',
      source: URD_VEHICLES,
    },
    {
      kind: 'tip',
      body: 'www.guide.d02.callout.fleetPortal',
      source: 'specs/user-requirements-document.md#epic-27-new-2026-07-18-change-set-fleet-portal-payout-vehicle-document-detail',
    },
  ],

  screens: ['SCR-DA-026', 'SCR-DA-004', 'SCR-DA-004a'],
  relatedChapters: ['d03', 'd04', 'd07'],
  faqRefs: ['become-a-driver', 'vehicle-types'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-23-new-driver-registration-phone-code-profile-setup-reach-home-then-optional-mode-c-vehicle-onboarding-auto-verified',
    'specs/D1_mageride_user_flows.md#phase-2-mode-c-vehicle-onboarding-optional-in-app-auto-verified',
    URD_VEHICLES,
    MCS_06,
    'specs/D2_mageride_ui_spec.md#scr-da-004-scr-di-004-vehicle_onboard_step1-vehicle-onboarding-step-1-4-mode-c-replace-change-6-22',
    'specs/D2_mageride_ui_spec.md#scr-da-026-scr-di-026-vehicle_mgmt-vehicle-management-switcher-adapt-us-2-8-2-16-us-9-6-us-13-9',
  ],
};
