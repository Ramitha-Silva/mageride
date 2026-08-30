/**
 * Passenger chapter 12 — sending a package.
 *
 * A package is a **Mode C extension**, not a fourth mode: the same dispatch, the
 * same 15-second offer, the same tariff, and deliveries counted with rides for the
 * driver's daily fee (URD Epic 20 rationale; D5 §11, P-06). The chapter says so in
 * the first line, because a reader who thinks it is a separate courier product will
 * expect a courier product's guarantees.
 *
 * ## Three numbers, and one of them is a different OTP from the sign-in one
 *
 * The pickup and delivery codes are **4 digits** (D5 §11, P-07; §14.1's validation
 * table lists "Package OTP | 4-digit HMAC, max 5 attempts"), and the **sign-in OTP
 * is 6** since AL-60. They are two different things in one app and it is worth not
 * blurring them: this chapter says four, and chapter 1 says six.
 *
 * **Max 5 attempts** each, then the step locks and goes to an admin queue. That is
 * published because it is the difference between "the driver keeps trying" and "we
 * are now both stuck and need Support", which a sender standing on a pavement needs
 * to know.
 *
 * ## The driver's side is three stages, and the last label changed
 *
 * US-20.12 makes the driver's flow a **three-stage bottom sheet** — review & start →
 * pickup OTP → delivery OTP + photo proof — and **US-20.13 replaced "Cash received
 * (COD)" with "Delivery completed"**. The old label is still quoted in places, so it
 * is named here as retired rather than left to be reintroduced by a future session
 * reading an older line. This chapter describes the driver's stages only as far as a
 * sender sees them; the driver's own chapter is S11's.
 *
 * ## The payment rails are chapter 11's corrected set, plus COD
 *
 * URD US-20.8 and the Epic 20 payment table still read *"LankaQR (no surcharge),
 * OnePay (+5%), or COD"* — the same staleness chapter 11 documents, listed at MCS-35
 * Scope B lines 806 and 816–819. The surviving rails are **cash, the passenger
 * wallet, the driver's own QR, and COD** (`POST /fare/pay` = `cash | wallet |
 * scan_driver_qr | cod`), none of them carrying a surcharge. COD is the only one
 * that is genuinely package-shaped: the **recipient** pays the driver on the
 * doorstep, and an uncollected COD past 24 hours moves the ride to `Disputed`
 * (P-14, D5 §8.3). That 24-hour rule is a real consequence for a sender and is a
 * callout rather than a footnote.
 *
 * ## The recipient needs no app, and the web page is the proof
 *
 * When the driver confirms pickup, a registered recipient gets a push and an
 * unregistered one gets an SMS link to `passenger.mageride.lk` — the same map, the
 * same status bar and the same delivery code, with no login and no account
 * (US-20.5, AL-21/AL-04). SCR-WT-002 is that page and SCR-WT-005 is the outcome
 * view; both are in the registry, so the claim is illustrated rather than asserted.
 *
 * **Not published:** the Walkthrough's own spec gap — the size hints give weight
 * guidance (S ≤ 5 kg, M ≤ 20 kg, L over 20 kg) but **no absolute maximum hard-blocks
 * a booking**. The chapter gives the hints as hints, which is what they are, and
 * promises no upper limit.
 */

import type { Chapter } from '@/content/types';

const URD_PACKAGES = 'specs/user-requirements-document.md#epic-20-new-package-delivery-mode-c-extension';
const D5_PACKAGES = 'specs/D5_mageride_business_logic.md#11-package-delivery-new-p-06-p-11';

export const p12: Chapter = {
  id: 'p12',
  slug: 'sending-a-package',
  audience: 'passenger',
  order: 12,
  title: 'www.guide.p12.title',
  summary: 'www.guide.p12.summary',

  steps: [
    { instruction: 'www.guide.p12.step1', screenRef: 'SCR-PA-012' },
    { instruction: 'www.guide.p12.step2', screenRef: 'SCR-PA-012' },
    {
      instruction: 'www.guide.p12.step3',
      note: 'www.guide.p12.step3.note',
      screenRef: 'SCR-PA-012',
    },
    { instruction: 'www.guide.p12.step4' },
    { instruction: 'www.guide.p12.step5', screenRef: 'SCR-PA-020' },
    { instruction: 'www.guide.p12.step6', screenRef: 'SCR-WT-002' },
    { instruction: 'www.guide.p12.step7' },
    { instruction: 'www.guide.p12.step8' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.p12.callout.fiveAttempts',
      source: D5_PACKAGES,
    },
    {
      kind: 'fee',
      body: 'www.guide.p12.callout.cod',
      source: 'specs/D5_mageride_business_logic.md#8-3-cod-lifecycle-p-08-new',
    },
    {
      kind: 'tip',
      body: 'www.guide.p12.callout.noAppNeeded',
      source: URD_PACKAGES,
    },
  ],

  screens: ['SCR-PA-012', 'SCR-PA-020', 'SCR-WT-002'],
  relatedChapters: ['p07', 'p11'],
  faqRefs: ['how-to-pay', 'vehicle-types'],

  sources: [
    URD_PACKAGES,
    D5_PACKAGES,
    'specs/D5_mageride_business_logic.md#8-3-cod-lifecycle-p-08-new',
    'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59',
    'specs/MageRide_Functional_Walkthrough.md#scenario-6-sending-a-package-delivery',
    'specs/MageRide_Functional_Walkthrough.md#scenario-7-tracking-a-package-you-have-sent-sender-view',
    'specs/MageRide_Functional_Walkthrough.md#scenario-8-receiving-a-package-with-the-app-and-without-the-app',
    'specs/MageRide_Functional_Walkthrough.md#scenario-106-the-delivery-ends-and-everyone-can-prove-it',
  ],
};
