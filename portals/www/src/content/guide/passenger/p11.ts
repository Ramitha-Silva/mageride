/**
 * Passenger chapter 11 — paying.
 *
 * ## S09's brief describes rails that were retired, and this is the chapter that
 * would have published a price the platform does not charge
 *
 * The brief says *"three ways to pay: cash (default), driver QR / LankaQR (no
 * surcharge), OnePay (+5%)"*. That was URD v2.9. **ADD v3.6's payment-custody change
 * set (2026-08-01, AL-57…AL-59) retired both card rails as ride methods**, and the
 * corrected answer is carried by four artefacts:
 *
 * - **D3** — `POST /fare/pay`'s method enum is `cash | wallet | scan_driver_qr | cod`,
 *   annotated *"AL-57/AL-59: onepay and platform lankaqr removed"*.
 * - **ADD §6 `fare-svc`** — the same four, *"because no ride fare may be charged to a
 *   platform merchant account"*.
 * - **D2's SCR-PA-016 paragraph**, marked `Δ AL-57/AL-59`.
 * - the built `apps/passenger-android`, whose `PaymentRails` contains neither.
 *
 * The rule the three changes reduce to, in ADD §1.18's own words: *OnePay and the
 * platform's own LankaQR merchant are used only where **MageRide is the payee***.
 * A ride fare's payee is the driver, so **no ride payment method carries a
 * surcharge** — OnePay's processing cost is recovered on a wallet **top-up**, where
 * MageRide legitimately is the payee. S08 reached this independently for chapter 8
 * and MCS-35 is the filed change set; this chapter is the one where getting it wrong
 * would have put "+5%" on a public page.
 *
 * ## Which is why `SCR-PA-016` is not one of this chapter's screens
 *
 * S08 reduced that registry entry to `passenger/paying` and left the judgement to
 * S09. The judgement is the same one, for the same reason: the approved wireframe —
 * and therefore the committed image — draws a live **"OnePay +5% Rs 43 · Rs 893"**
 * row against a Rs 850 fare. Publishing it beside copy that says there is no
 * surcharge contradicts the page in a picture, and a picture is what a reader
 * believes. Its `chapters` list is now empty, matching what S08 did to SCR-PA-025a,
 * and the frame is MCS-35 Scope A's first row.
 *
 * **`SCR-PA-017` is kept, and the distinction is deliberate.** That frame's subject
 * and primary action are exactly this chapter's and are correct after AL-59: "Scan
 * driver's QR to pay", the awaiting-confirmation state and "Switch to Cash". Its
 * secondary *"Pay with my bank app (LankaQR)"* row is the platform-merchant deep
 * link AL-59 retired for rides — a stale control rather than a false price, and a
 * **location MCS-35's Scope A table does not list** (it reaches SCR-PA-017 only
 * through D2's component tables). Recorded in the S09 handoff.
 *
 * ## Attestation is the honest word and the chapter uses it
 *
 * A payment into the **driver's own** bank QR moves bank-to-bank and produces **no
 * callback to the platform** — there is no webhook to wait for and nothing for
 * MageRide to verify. So it settles the way cash does: the passenger claims *"I've
 * paid"* (optionally attaching a receipt screenshot as dispute evidence), the driver
 * confirms *"QR payment received"*, and the ride reaches the terminal state
 * `DriverConfirmedQR` (US-26.1, AL-47). The driver may also confirm without a claim;
 * an unconfirmed claim nudges the driver and then routes to Support, where a person
 * looks at it. **No money moves in a dispute** — the platform never held it.
 *
 * The `wallet` rail is the opposite shape and the copy keeps them apart: a prepaid
 * balance, one balanced ledger entry, terminal on the spot, no gateway leg at all.
 *
 * **Not published, on purpose:** AL-58's driver payout run, the CBSL/sponsor-bank
 * go-live gate, and the Finance dispute queue's internals. S09's fence — *do not
 * describe a payout, refund or dispute mechanism the specs do not define* — and
 * these are defined but are not a passenger's business. The reader is told disputes
 * go to Support; the machinery behind Support is not a public claim.
 *
 * **Known gap:** the passenger wallet has no `SCR-PA-*` screen. D2 line 1019
 * discharges AL-57 with *"the same screen serves the passenger"*, pointing at
 * SCR-DA-022 — a **driver** screen a passenger cannot open. It is MCS-35 decision
 * D4, and it is why the wallet is described in words here and illustrated by nothing.
 */

import type { Chapter } from '@/content/types';

const ADD_CUSTODY = 'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59';
const URD_QR_ATTESTATION = 'specs/user-requirements-document.md#epic-26-new-2026-07-05-change-set-2-driver-qr-settlement-number-masking-removal';

export const p11: Chapter = {
  id: 'p11',
  slug: 'paying',
  audience: 'passenger',
  order: 11,
  title: 'www.guide.p11.title',
  summary: 'www.guide.p11.summary',

  steps: [
    { instruction: 'www.guide.p11.step1' },
    { instruction: 'www.guide.p11.step2' },
    { instruction: 'www.guide.p11.step3' },
    {
      instruction: 'www.guide.p11.step4',
      note: 'www.guide.p11.step4.note',
      screenRef: 'SCR-PA-017',
    },
    { instruction: 'www.guide.p11.step5', screenRef: 'SCR-PA-017' },
    { instruction: 'www.guide.p11.step6' },
    { instruction: 'www.guide.p11.step7', screenRef: 'SCR-PA-018' },
    { instruction: 'www.guide.p11.step8' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.p11.callout.noSurcharge',
      source: ADD_CUSTODY,
    },
    {
      kind: 'warning',
      body: 'www.guide.p11.callout.attestation',
      source: URD_QR_ATTESTATION,
    },
  ],

  screens: ['SCR-PA-017', 'SCR-PA-018'],
  relatedChapters: ['p08', 'p10'],
  faqRefs: ['how-to-pay', 'driver-keeps', 'passenger-cost'],

  sources: [
    ADD_CUSTODY,
    URD_QR_ATTESTATION,
    'specs/D3_mageride_api_contracts.md#fare-svc-ride-payment',
    'specs/D5_mageride_business_logic.md#8-1-payment-state-machine-d-10-r-19-p-08',
    'specs/D2_mageride_ui_spec.md#scr-pa-016-payment-method-payment-selection',
    'specs/MageRide_Functional_Walkthrough.md#scenario-107-a-passenger-pays-the-drivers-qr-and-both-sides-confirm-it',
    'specs/MageRide_Functional_Walkthrough.md#scenario-17-viewing-trip-details-and-downloading-a-receipt',
  ],
};
