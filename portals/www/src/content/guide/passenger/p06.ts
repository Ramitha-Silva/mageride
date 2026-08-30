/**
 * Passenger chapter 6 — paying for a private vehicle you follow (Mode B).
 *
 * ## The label, and the word this chapter does not use
 *
 * US-27.4 renamed the Paid/Free setting to **"Service payment"** — a UI and
 * documentation label only; the API path and the database column are deliberately
 * unchanged. So the chapter says *Service payment*, says *Paid* and *Free*, and never
 * says the old phrase.
 *
 * ## Four payment methods, not five
 *
 * The Walkthrough, D2's screen table and the approved wireframe for SCR-PA-025a were
 * all written when the subscription pay sheet offered **OnePay (+5%)**. ADD v3.6
 * **AL-59 removed it**: a subscription is pass-through to the fleet owner, and OnePay
 * would land subscriber money in MageRide's own account. D2's SCR-PA-025a row carries
 * the correction (`Δ AL-59: OnePay removed`) and D3's `POST /mode-b/subscriptions/
 * {id}/pay` enumerates exactly `lankaqr_deeplink | lankaqr_scan | online_transfer |
 * cash`. This chapter follows the current specs and lists those four.
 *
 * **That is why `SCR-PA-025a` is not in {@link screens}.** The composited image is a
 * faithful rendering of the approved wireframe, and the approved wireframe shows an
 * "OnePay · cards / wallets · +5%" row that the platform no longer offers. Publishing
 * it beside this chapter would quote a surcharge that does not exist, on a page whose
 * whole job is to be trusted about money. The frame stays in the registry (it is still
 * the subscription pay screen, and `/screens` is a different question) and this is
 * recorded for whoever re-renders the wireframes.
 *
 * ## Two different monthly amounts, and the chapter keeps them apart
 *
 * The **subscription** is what a passenger pays the operator; the operator separately
 * pays MageRide *approximately* Rs 300 per private vehicle. The URD says
 * "approximately" in both places it states the second figure, so the copy says "about"
 * — the same rule `marketing.ts` records for the fee band.
 */

import type { Chapter } from '@/content/types';

const URD_MODE_B_PAYMENTS = 'specs/user-requirements-document.md#epic-23';

export const p06: Chapter = {
  id: 'p06',
  slug: 'mode-b-payments',
  audience: 'passenger',
  order: 6,
  title: 'www.guide.p06.title',
  summary: 'www.guide.p06.summary',

  steps: [
    { instruction: 'www.guide.p06.step1' },
    { instruction: 'www.guide.p06.step2' },
    { instruction: 'www.guide.p06.step3', screenRef: 'SCR-PA-025' },
    { instruction: 'www.guide.p06.step4', screenRef: 'SCR-PA-025' },
    { instruction: 'www.guide.p06.step5' },
    { instruction: 'www.guide.p06.step6' },
    { instruction: 'www.guide.p06.step7' },
    { instruction: 'www.guide.p06.step8' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.p06.callout.passThrough',
      source: URD_MODE_B_PAYMENTS,
    },
    {
      kind: 'fee',
      body: 'www.guide.p06.callout.firstMonth',
      source: 'specs/user-requirements-document.md#epic-4',
    },
  ],

  screens: ['SCR-PA-025'],
  relatedChapters: ['p05'],
  faqRefs: ['mode-b-price', 'mode-b-access'],

  sources: [
    URD_MODE_B_PAYMENTS,
    'specs/user-requirements-document.md#epic-27',
    'specs/MageRide_Functional_Walkthrough.md#scenario-14-managing-mode-b-subscriptions-viewing-and-unsubscribing',
    'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59',
    'specs/D3_mageride_api_contracts.md#subscription-svc-mode-b-subscriptions',
  ],
};
