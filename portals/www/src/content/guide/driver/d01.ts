/**
 * Driver chapter 1 — install and first run.
 *
 * Written from Walkthrough scenario 23 and D1 §B.7 Phase 1, which are the same
 * sequence told twice: language and city, phone and code, profile setup,
 * permissions, Home.
 *
 * ## The driver *does* choose a city, and the passenger does not
 *
 * Passenger chapter 1 had to drop the city step, because US-1.3a is written "as a
 * user" but the screen carrying the city list is the driver's. This is that screen
 * — **SCR-DA-002 · language and city** — so the step belongs here and only here.
 * The list is loaded from the backend (`config.operating_cities`, `GET
 * /config/cities`), which is why the guide says the cities are the ones MageRide has
 * launched in rather than naming any: an admin can add one without an app release,
 * and a public page that listed them would be wrong the first time that happened.
 *
 * ## What the licence scan reads is a driver-facing fact, not a technical one
 *
 * US-2.4a: the scan extracts **NIC number and allowed vehicle types** alongside the
 * licence number and expiry. It matters to the reader because of the consequence —
 * **anything the scan cannot read, the driver types, and anything the driver types
 * is flagged for a Verification Officer** before it is trusted. A driver who does
 * not know that will not understand why a clear photograph is worth taking twice.
 * Chapter 3 is the photography; chapter 4 is the waiting.
 *
 * **The apps are not published** (MCS-34 D3), so this chapter cannot open with "get
 * it from the store" any more than the passenger's could.
 */

import type { Chapter } from '@/content/types';

const URD_ONBOARDING = 'specs/user-requirements-document.md#epic-1-user-registration-onboarding';
const D1_PHASE_1 = 'specs/D1_mageride_user_flows.md#phase-1-driver-identity-precedes-home-no-vehicle-required';

export const d01: Chapter = {
  id: 'd01',
  slug: 'install-and-first-run',
  audience: 'driver',
  order: 1,
  title: 'www.guide.d01.title',
  summary: 'www.guide.d01.summary',

  steps: [
    { instruction: 'www.guide.d01.step1', screenRef: 'SCR-DA-002' },
    {
      instruction: 'www.guide.d01.step2',
      note: 'www.guide.d01.step2.note',
      screenRef: 'SCR-DA-002',
    },
    { instruction: 'www.guide.d01.step3', screenRef: 'SCR-DA-003' },
    { instruction: 'www.guide.d01.step4', screenRef: 'SCR-DA-003a' },
    {
      instruction: 'www.guide.d01.step5',
      note: 'www.guide.d01.step5.note',
      screenRef: 'SCR-DA-003a',
    },
    { instruction: 'www.guide.d01.step6' },
    { instruction: 'www.guide.d01.step7' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d01.callout.notPublished',
      source: 'build/prompts/MCS-34-www-informational-site.md#decisions-taken-d1-d10',
    },
    {
      // US-2.21 / D1 §B.7 Phase 1 step 4. Worth stating plainly at the top of the
      // guide: a driver who thinks they need a registered vehicle before they can
      // even sign in will put the whole thing off until they have the paperwork.
      kind: 'tip',
      body: 'www.guide.d01.callout.noVehicleNeeded',
      source: D1_PHASE_1,
    },
    {
      // US-1.12/1.13, and the half of it that is specific to drivers: the rule is
      // per app, so the same person may run the driver app and the passenger app at
      // the same time. A driver who reads "one device" as "one account" would think
      // they have to log out to book their own ride home.
      kind: 'warning',
      body: 'www.guide.d01.callout.oneDevicePerApp',
      source: URD_ONBOARDING,
    },
  ],

  screens: ['SCR-DA-002', 'SCR-DA-003', 'SCR-DA-003a'],
  relatedChapters: ['d02', 'd03', 'd05'],
  faqRefs: ['become-a-driver', 'signup', 'languages'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-23-new-driver-registration-phone-code-profile-setup-reach-home-then-optional-mode-c-vehicle-onboarding-auto-verified',
    D1_PHASE_1,
    URD_ONBOARDING,
    'specs/user-requirements-document.md#epic-2-vehicle-registration',
    'specs/D2_mageride_ui_spec.md#scr-da-003a-scr-di-003a-profile_setup-driver-profile-setup-primary-new-change-6-22',
  ],
};
