/**
 * Passenger chapter 10 — during the ride.
 *
 * ## The privacy sentence in this chapter is the one to get exactly right
 *
 * **Number masking was withdrawn.** URD v2.7 Epic 26 / ADD v3.1 §1.13 (AL-47…AL-48)
 * closed feasibility condition C3 by *removing the requirement*, not by engineering
 * around it: there is no proxy-DID or CPaaS product with +94 numbers, so matched
 * parties see each other's real mobile numbers instead. The call chooser survives —
 * **Free call** is an in-app internet call, **Normal call** is a direct cellular
 * dial of the real number (US-26.2, SCR-PA-015a) — and the number is revealed
 * **only after a driver accepts** and is **withheld for rides cancelled before
 * assignment**.
 *
 * That supersedes the masking clauses of US-6A.16/16a, US-8.22, US-11.9, US-24.3 and
 * US-25.4, and it supersedes the masked-SMS relay fallback (D-25) too: a Free call
 * that will not connect now offers *"Call normally instead?"* (US-26.4). Several
 * of those superseded stories are still in the URD carrying their old wording, and
 * `specs/D5_mageride_business_logic.md` §10 still says the driver's offer carries a
 * "masked phone" — so a session writing this chapter from a single grep would
 * publish a privacy promise the platform does not keep. **P-05 is the part that is
 * retained**: on a proxy booking the driver sees the *rider's* number, never the
 * booker's, and that is chapter 13's.
 *
 * The chapter therefore states number visibility as a plain fact with its anchor,
 * rather than reassuring anybody. US-26.5 requires the same disclosure at sign-up and
 * on the first call, and `www.faq.phoneNumber.a` already says it in the same words.
 *
 * ## The Rs 50 is stated once on this site, and it is stated here
 *
 * Chapter 9 owns "free before a driver accepts" because that is drawn on
 * SCR-PA-014. This chapter owns the **Rs 50 after acceptance** (US-6A.9/6A.10),
 * settled against the next trip and paid on to the driver whose accepted ride was
 * cancelled, plus **three consecutive post-acceptance cancellations disable
 * booking** (US-6A.10b) — consecutive, and the counter resets on any completed ride,
 * which is the half a reader needs and the half most likely to be dropped.
 *
 * **Not published:** the Walkthrough records a spec gap — what a passenger sees if
 * they never take another trip and the Rs 50 stays outstanding is undefined. The
 * copy says it is added to the next ride and stops there.
 *
 * ## There is no "share my trip" step, and that is a finding rather than an omission
 *
 * The plan's chapter table lists *share* among the during-the-ride actions.
 * `safety.trip_share_tokens` does carry a `trip_view` scope (D-34) and the ADD's
 * implementation checklist names a "live-trip web share token" — but **no URD story,
 * no D2 screen row and no wireframe control surfaces it**. SCR-PA-015 draws the
 * driver card, the start code, Call, SOS and Cancel, and nothing else. A step naming
 * a Share button would violate S08's fence — a step that references a control must
 * reference a screen that has it — so the chapter describes what the screen does.
 * The gap is recorded in the S09 handoff, because `www.faq.safety.a` (S07) already
 * promises the reader they can share a live trip.
 */

import type { Chapter } from '@/content/types';

const URD_MASKING_REMOVED = 'specs/user-requirements-document.md#epic-26-new-2026-07-05-change-set-2-driver-qr-settlement-number-masking-removal';
const URD_DISPATCH = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';

export const p10: Chapter = {
  id: 'p10',
  slug: 'during-the-ride',
  audience: 'passenger',
  order: 10,
  title: 'www.guide.p10.title',
  summary: 'www.guide.p10.summary',

  steps: [
    { instruction: 'www.guide.p10.step1', screenRef: 'SCR-PA-015' },
    { instruction: 'www.guide.p10.step2', screenRef: 'SCR-PA-015' },
    { instruction: 'www.guide.p10.step3', screenRef: 'SCR-PA-015' },
    {
      instruction: 'www.guide.p10.step4',
      note: 'www.guide.p10.step4.note',
    },
    { instruction: 'www.guide.p10.step5' },
    { instruction: 'www.guide.p10.step6', screenRef: 'SCR-PA-029' },
    { instruction: 'www.guide.p10.step7' },
    { instruction: 'www.guide.p10.step8' },
  ],

  callouts: [
    {
      kind: 'privacy',
      body: 'www.guide.p10.callout.realNumbers',
      source: URD_MASKING_REMOVED,
    },
    {
      kind: 'fee',
      body: 'www.guide.p10.callout.cancelAfterAccept',
      source: URD_DISPATCH,
    },
    {
      kind: 'warning',
      body: 'www.guide.p10.callout.sosContact',
      source: 'specs/user-requirements-document.md#epic-12-safety-trust',
    },
  ],

  screens: ['SCR-PA-015', 'SCR-PA-029'],
  relatedChapters: ['p09', 'p11'],
  faqRefs: ['phone-number', 'safety'],

  sources: [
    URD_MASKING_REMOVED,
    URD_DISPATCH,
    'specs/user-requirements-document.md#epic-12-safety-trust',
    'specs/D1_mageride_user_flows.md#f-30-2-calling-without-masking-items-2-4-us-26-2-26-3-26-4',
    'specs/architecture-design-document.md#1-13-remediation-log-al-47-al-48',
    'specs/MageRide_Functional_Walkthrough.md#scenario-108-calls-now-use-real-numbers',
    'specs/MageRide_Functional_Walkthrough.md#scenario-10-cancelling-a-ride-after-a-driver-is-assigned-the-rs-50-penalty',
    'specs/MageRide_Functional_Walkthrough.md#scenario-20-using-sos-as-a-passenger-during-an-active-ride',
  ],
};
