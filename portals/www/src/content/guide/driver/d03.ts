/**
 * Driver chapter 3 — photographing documents.
 *
 * One screen (SCR-DA-005) reached from every 📷 slot in onboarding, and one skill
 * worth teaching properly: **drag the four corners so the whole document fills the
 * frame.** AL-43's stated reason is the reason the chapter exists — a better capture
 * raises extraction confidence, and higher confidence means fewer fields land in the
 * Verification Officer's queue. That is the difference between a vehicle approved by
 * a machine in minutes and a vehicle waiting on a person.
 *
 * ## S10's brief names the Fleet Portal's document slots, and this is the driver app
 *
 * The brief says the per-vehicle slots are *"registration copy (CR book), insurance
 * certificate, revenue licence, route permit (Mode A required)"* citing **US-27.3**.
 * US-27.3 is **SCR-FP-004 — the Fleet Portal's** vehicle onboarding, and **US-2.24
 * removed the registration document and the permit from driver-app onboarding
 * outright**. The driver app's slots are the four this chapter lists: the driving
 * licence front and back at profile setup, then insurance, revenue licence, and
 * front-and-back vehicle photos with the plate readable.
 *
 * Publishing the Fleet Portal's list here would send an owner-driver looking for a
 * CR-book slot that is not in their app. The chapter names the driver-app four and
 * says in one line where the other two live, which is the useful half of the brief's
 * intent. **Insurance is mandatory for every mode** (US-2.19) and that part is right
 * and is stated.
 *
 * ## What each document is read for
 *
 * Named per document because a driver photographing an insurance certificate should
 * know the app is looking for the **expiry date**, and a revenue licence for its
 * **number and expiry**. The vehicle photos are matched against the registration
 * number typed at step 1 — a plate that cannot be read, or that does not match, sets
 * that step Pending (US-2.10a). Chapter 4 is what Pending means.
 *
 * **Not published:** that extraction is Gemini Flash, that redaction happens in the
 * perimeter (D-36), or any confidence threshold. A driver needs to know a machine
 * reads the photograph and a person checks what it could not; the vendor and the
 * threshold are neither their business nor stable.
 */

import type { Chapter } from '@/content/types';

const URD_DRAG_CROP = 'specs/user-requirements-document.md#epic-24-new-2026-06-28-change-set-ux-admin-directory';
const URD_VEHICLES = 'specs/user-requirements-document.md#epic-2-vehicle-registration';

export const d03: Chapter = {
  id: 'd03',
  slug: 'photographing-documents',
  audience: 'driver',
  order: 3,
  title: 'www.guide.d03.title',
  summary: 'www.guide.d03.summary',

  steps: [
    { instruction: 'www.guide.d03.step1', screenRef: 'SCR-DA-005' },
    {
      instruction: 'www.guide.d03.step2',
      note: 'www.guide.d03.step2.note',
      screenRef: 'SCR-DA-005',
    },
    { instruction: 'www.guide.d03.step3', screenRef: 'SCR-DA-005' },
    { instruction: 'www.guide.d03.step4', screenRef: 'SCR-DA-003a' },
    { instruction: 'www.guide.d03.step5', screenRef: 'SCR-DA-004a' },
    { instruction: 'www.guide.d03.step6' },
    { instruction: 'www.guide.d03.step7', note: 'www.guide.d03.step7.note' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d03.callout.whyItMatters',
      source: URD_DRAG_CROP,
    },
    {
      // US-2.19/2.20 and the E-03 rule behind them. A fee callout in all but name:
      // an expired certificate stops a driver earning, and the app is where the
      // renewal goes back in (US-2.11).
      kind: 'warning',
      body: 'www.guide.d03.callout.insuranceMandatory',
      source: URD_VEHICLES,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d03.callout.whatIsRead',
      source: URD_VEHICLES,
    },
  ],

  screens: ['SCR-DA-005', 'SCR-DA-003a', 'SCR-DA-004a'],
  relatedChapters: ['d02', 'd04'],
  faqRefs: ['become-a-driver', 'my-data'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-102-a-driver-captures-a-document-by-fitting-it-in-the-frame',
    URD_DRAG_CROP,
    URD_VEHICLES,
    'specs/D1_mageride_user_flows.md#f-28-4-driver-document-capture-with-drag-crop-item-6-us-24-6',
    'specs/D5_mageride_business_logic.md#br-28-4-onboarding-camera-capture-drag-crop-item-6-al-43',
    'specs/D2_mageride_ui_spec.md#scr-da-di-005-document-capture-camera-draggable-corner-crop',
  ],
};
