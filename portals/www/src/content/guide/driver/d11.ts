/**
 * Driver chapter 11 — package jobs.
 *
 * URD Epic 20, Walkthrough scenarios 29/30/31, and the three SCR-DA-016a/b/c sheets
 * as D2 draws them.
 *
 * ## Three sheets, two codes, and one button that used to be a different button
 *
 * The delivery flow is a **three-stage bottom-sheet sequence** (US-20.12) and each
 * stage is a different set of controls, so the chapter is written as three stages
 * rather than as one procedure with branches:
 *
 * 1. **Review and start** — the pickup and drop distances, the payment method, and
 *    *both* phone numbers with a Call button each. **Cancel here re-dispatches the
 *    job to the next eligible driver**, which is the useful half: a driver who does
 *    not want a parcel should decline it at review rather than after collecting it.
 * 2. **Pickup** — the map, Call sender, SOS, and the sender's **4-digit Pickup OTP**.
 * 3. **Complete** — the recipient's **4-digit Delivery OTP** *or* **photo proof**,
 *    both numbers again, and **Delivery completed**.
 *
 * **US-20.13 replaced the old "Cash received (COD)" button with "Delivery
 * completed"**, and the guide says so in as many words. A driver who has read an
 * older description will look for a cash button, not find it, and conclude the
 * delivery cannot be completed — which is exactly the support call the sentence
 * prevents.
 *
 * ## The rails are named the way chapter 9 names them, which is not at all
 *
 * The review sheet shows the passenger's chosen payment method, and the one thing
 * this chapter states about it is **COD: the recipient pays cash at the door**
 * (US-20.8). It does not enumerate the others, for the reason `d09` records:
 * **MCS-35 decision D3 — the C-11 label standard — is open**, and the retired
 * `OnePay / LankaQR` ride labels must not be printed on a public page. URD Epic 20's
 * own "three methods" sentence is on MCS-35's Scope B list for exactly this reason
 * (`build/prompts/MCS-35-retired-ride-rails-wireframes-and-urd.md`, line 806).
 *
 * ## The two gaps, and how each is written
 *
 * - **COD with an absent recipient is a spec gap** the Walkthrough raises itself
 *   (scenario 30): photo proof stands in for the delivery code, and nothing says
 *   whether that is allowed when cash still has to be collected. The chapter gives
 *   the Walkthrough's own instruction — *do not leave a COD parcel and do not
 *   complete it; contact support* — and invents no rule.
 * - **Uncollected COD past 24 hours makes the delivery Disputed** (P-14). That is a
 *   real consequence for the driver and is stated, because the alternative is a
 *   driver discovering it from a dispute rather than from the guide.
 *
 * ## What is the same as a passenger ride, said once
 *
 * Same dispatch and the same fifteen seconds; **the same fare tariff** (US-20.9);
 * and **the same daily fee, counted together with passenger rides** (US-20.10) — so
 * a delivery can be the free first trip of the day, and a delivery can be the second
 * trip that triggers the charge. Package delivery is **not restricted by vehicle
 * type** (Epic 20's rationale); truck and mini truck exist *in addition to* the
 * passenger types, not instead of them.
 */

import type { Chapter } from '@/content/types';

const URD_PACKAGES =
  'specs/user-requirements-document.md#epic-20-new-package-delivery-mode-c-extension';
const URD_DISPATCH =
  'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';

export const d11: Chapter = {
  id: 'd11',
  slug: 'package-jobs',
  audience: 'driver',
  order: 11,
  title: 'www.guide.d11.title',
  summary: 'www.guide.d11.summary',

  steps: [
    { instruction: 'www.guide.d11.step1', note: 'www.guide.d11.step1.note' },
    { instruction: 'www.guide.d11.step2', screenRef: 'SCR-DA-016a' },
    {
      instruction: 'www.guide.d11.step3',
      note: 'www.guide.d11.step3.note',
      screenRef: 'SCR-DA-016a',
    },
    { instruction: 'www.guide.d11.step4', screenRef: 'SCR-DA-016b' },
    { instruction: 'www.guide.d11.step5', screenRef: 'SCR-DA-016b' },
    { instruction: 'www.guide.d11.step6', screenRef: 'SCR-DA-016c' },
    { instruction: 'www.guide.d11.step7', screenRef: 'SCR-DA-016c' },
    { instruction: 'www.guide.d11.step8', screenRef: 'SCR-DA-016c' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d11.callout.codAndAbsentRecipient',
      source: URD_PACKAGES,
    },
    {
      kind: 'fee',
      body: 'www.guide.d11.callout.sameFeeSameTariff',
      source: URD_PACKAGES,
    },
    {
      kind: 'tip',
      body: 'www.guide.d11.callout.cancelAtReview',
      source: URD_PACKAGES,
    },
  ],

  screens: ['SCR-DA-016a', 'SCR-DA-016b', 'SCR-DA-016c'],
  relatedChapters: ['d08', 'd09', 'd10'],
  faqRefs: ['daily-fee', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-29-receiving-a-package-delivery-request-the-three-delivery-bottom-sheets-review-pickup-otp-complete',
    'specs/MageRide_Functional_Walkthrough.md#scenario-30-photo-proof-of-delivery-when-the-recipient-is-unavailable',
    'specs/MageRide_Functional_Walkthrough.md#scenario-31-cash-on-delivery-cod-collecting-cash-and-completing-the-delivery',
    URD_PACKAGES,
    URD_DISPATCH,
    URD_FEES,
    'specs/D2_mageride_ui_spec.md#scr-da-016-scr-di-016-delivery_confirm-package-fulfilment-new-us-20-4-20-6-p-07',
    'build/prompts/MCS-35-retired-ride-rails-wireframes-and-urd.md#the-decision',
  ],
};
