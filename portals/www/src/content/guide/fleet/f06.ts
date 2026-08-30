/**
 * Fleet chapter 6 — billing: a monthly charge per Mode B vehicle.
 *
 * ## The fence this chapter was written under
 *
 * S23: *"Fleet monthly and driver daily are different fee models. Never one paragraph
 * covering both."* They are two callouts here, not one, and the second exists only to
 * say where the *other* model lives — because the single most likely error a reader
 * makes on this page is to add the daily platform fee to their fleet invoice in their
 * head.
 *
 * URD §13.G states the rule in one place and it is quoted almost intact into the
 * copy: **Mode A vehicles are always free. Mode B vehicles are charged a monthly fee
 * per vehicle. Mode C is not a fleet option** — Mode C daily fees are always paid
 * from the *individual driver's* wallet in the Driver App (Epic 9), never from a
 * fleet wallet.
 *
 * ## No rupee figure is written in this chapter's prose, and that is d13's rule
 *
 * Driver chapter 13 states the reason at length: an amount typed into `en.ts` becomes
 * three amounts once `si.ts` and `ta.ts` are written, in files no fee test reads, and
 * the first one to drift is a commercial claim to somebody deciding how to run a
 * business. The Mode B monthly figure is additionally **approximate in the URD's own
 * words** — "a monthly charge of *approximately* Rs 300", in all four places it
 * appears — and `src/content/marketing.ts` records why an approximate price rendered
 * as a precise one is a false claim with a decimal point on it. It is published on
 * this site in exactly one place, S07's fee band, and this chapter states the *rule*
 * and points at the invoice, which is where the owner's own numbers actually are.
 *
 * ## Money in is not money out
 *
 * The last step is the one an owner is most likely to have muddled, and it is
 * anchored: subscription payments from Mode B passengers are **pass-through to the
 * fleet owner** (Walkthrough Scenario 85's note; US-27.2) and route to the verified
 * payout profile of chapter 2. They are not credited against this invoice, and
 * MageRide holds none of that money. Two directions, two chapters, said once each.
 *
 * ## The FAQ subset is two entries, and the one left out was left out on purpose
 *
 * `why-free` and `daily-fee` both *reinforce* the separation this chapter is built
 * around: each is explicitly about the on-demand driver's fee, so a fleet owner
 * reading them is being told again whose fee that is. **`mode-b-price` is not here,
 * and it was, until the built page was read.** That answer carries "around Rs 300 per
 * vehicle" — the one place this site publishes a Mode B figure — and rendering it
 * under a heading that says *billing* puts a rupee amount at the foot of the chapter
 * whose entire fence is that two fee models must not blur, in the one section of the
 * page this chapter does not write. It belongs on chapter 2, where the subject
 * genuinely is what a subscriber pays, and it is there.
 *
 * That is also the argument for the prose rule above being about *prose*: the figure
 * still reaches a reader who wants it, from the single module that owns it, through a
 * reference rather than a copy.
 *
 * ## Top-up methods are enumerated because one is deliberately absent
 *
 * **Card, OnePay, LankaQR** (US-13.10b) — and *no bank transfer*, which the URD's v2.2
 * pass removed platform-wide and Scenario 79's edge table repeats as a thing an owner
 * will expect and not find. An absence a reader will look for is worth a sentence.
 */

import type { Chapter } from '@/content/types';

const URD_BILLING = 'specs/user-requirements-document.md#13g-fleet-billing';
const URD_VISION = 'specs/user-requirements-document.md#1-product-vision';
const URD_ACCESS = 'specs/user-requirements-document.md#13a-fleet-portal-access-authentication';
const URD_PAYOUT =
  'specs/user-requirements-document.md#epic-27-new-2026-07-18-change-set-fleet-portal-payout-vehicle-document-detail';
const WALKTHROUGH_BILLING =
  'specs/MageRide_Functional_Walkthrough.md#scenario-79-fleet-billing-monthly-per-mode-b-vehicle-invoice-and-fleet-wallet-top-up';

export const f06: Chapter = {
  id: 'f06',
  slug: 'billing',
  audience: 'fleet',
  order: 6,
  title: 'www.guide.f06.title',
  summary: 'www.guide.f06.summary',

  steps: [
    { instruction: 'www.guide.f06.step1', screenRef: 'SCR-FP-003' },
    { instruction: 'www.guide.f06.step2', screenRef: 'SCR-FP-010' },
    { instruction: 'www.guide.f06.step3', note: 'www.guide.f06.step3.note' },
    { instruction: 'www.guide.f06.step4' },
    { instruction: 'www.guide.f06.step5' },
    { instruction: 'www.guide.f06.step6' },
    { instruction: 'www.guide.f06.step7', note: 'www.guide.f06.step7.note' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.f06.callout.whatTheFleetPays',
      source: URD_BILLING,
    },
    {
      kind: 'warning',
      body: 'www.guide.f06.callout.modeCIsNotYours',
      source: URD_BILLING,
    },
    {
      kind: 'fee',
      body: 'www.guide.f06.callout.moneyInIsSeparate',
      source: URD_PAYOUT,
    },
  ],

  screens: ['SCR-FP-003', 'SCR-FP-010'],
  relatedChapters: ['f02', 'f03', 'f01'],
  faqRefs: ['why-free', 'daily-fee'],

  sources: [
    URD_BILLING,
    URD_VISION,
    URD_ACCESS,
    URD_PAYOUT,
    WALKTHROUGH_BILLING,
    'specs/D2_mageride_ui_spec.md#scr-fp-010-fleetbilling-billing-wallet-new-us-131010b',
  ],
};
