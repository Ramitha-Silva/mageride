/**
 * Driver chapter 14 — getting paid.
 *
 * ## The one sentence this chapter exists to prevent being misread
 *
 * *"A weekly payout"* is true of the wallet and false of a cash fare, and a driver
 * who reads the first as covering the second has been misled about where their own
 * money is. So the chapter separates the three routes by **where the money physically
 * is**, and only then says what happens next to each:
 *
 * | How the passenger paid | Where the money goes | What MageRide does |
 * |---|---|---|
 * | **Cash** | the driver's hand | nothing — it never touches the platform |
 * | **The driver's own bank QR** | the driver's own bank account, bank to bank | nothing; it cannot even see that it arrived (AL-59) |
 * | **A MageRide balance** | the driver's wallet, as one balanced ledger entry | holds it, and discharges it through the AL-58 payout run |
 *
 * **`payout-svc` (C133) is the rail for wallet balances, and for nothing else.** A
 * driver who believes their cash rides arrive in a weekly payout will wait for money
 * they were handed days ago; a driver who believes their QR rides do will wait for
 * money that is already in their bank. Both readings are available from a page that
 * says "you are paid weekly" and neither is true.
 *
 * ## Driver-QR settles by attestation, and why that is not a hedge
 *
 * A payment scanned into the **driver's own** bank-issued LankaQR is bank-to-bank
 * and **produces no gateway callback to the platform** (AL-47/AL-59, US-26.1) — so
 * `Paid` cannot be gateway-verified and settlement is attestation instead, exactly
 * like cash. Passenger taps **"I've paid"** (optionally attaching a receipt
 * screenshot); the driver gets a **"QR payment received?"** prompt and on **Confirm**
 * the payment reaches the terminal state `DriverConfirmedQR`. **A driver may confirm
 * without a passenger claim.** A claim with no confirm nudges the driver and, if it
 * stays unresolved, routes to Support and then a Finance dispute — and **no money
 * moves**, because MageRide is holding none of it. The passenger guide's chapter 11
 * was written from the same paragraph and says the same thing from the other side.
 *
 * ## The earning posts on payment, not on End
 *
 * R-05, restated in `fare-svc` and in D5 §8.1: the driver's earning posts **only on
 * a terminal payment state**. That is why a finished trip can show its money a
 * moment after it finished, and saying so is cheaper than the support ticket that
 * asks why.
 *
 * ## What the payout run actually does — and the two things it needs
 *
 * From AL-58, D2's SCR-DA-022a and C133's own implementation (`PayoutRunService`:
 * *"Full sweep, no minimum, no holdback"*):
 *
 * - It runs **weekly** and pays **whatever the balance is on run day, in full**.
 * - It pays **only a driver with a `verified` bank & payout profile** — bank,
 *   branch, account number, account holder name, a bank statement **or** passbook
 *   first page, and the driver's own bank-app **LankaQR image**, approved by a
 *   Verification Officer. Any edit re-enters Pending.
 * - **A driver with no verified profile accrues and is never paid out.** The balance
 *   is retained and never lost, and they appear on Finance's exception queue.
 *
 * **And the honest half.** AL-58 states that the bank adapter is *"one outbound port
 * and no provider is chosen"*, that unconfigured the run leaves instructions
 * `PENDING`, and that origination *"requires a sponsor bank and CBSL authorisation —
 * a go-live gate, not an engineering task"*. C133's handoff says the same in its own
 * words. So the chapter states the schedule as the design and says plainly that
 * **MageRide will confirm when payouts begin**, and that until then the money a
 * driver has in hand the same day is cash and their own QR. Publishing "you are paid
 * every week" against an unconfigured bank port would be the exact class of promise
 * this session's fence forbids.
 *
 * ## The profile is also what makes driver-QR possible
 *
 * AL-59: the QR image on that profile *is* the code a passenger scans. So the same
 * screen that unlocks payouts is the one that unlocks the second way of being paid —
 * which is the practical reason to fill it in, and it is worth more to a driver than
 * the ledger explanation.
 */

import type { Chapter } from '@/content/types';

const URD_QR =
  'specs/user-requirements-document.md#epic-26-new-2026-07-05-change-set-2-driver-qr-settlement-number-masking-removal';
const ADD_CUSTODY = 'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59';
const ADD_PAYOUT = 'specs/architecture-design-document.md#11-9a-automated-driver-payout-run-al-58';
const URD_VISION = 'specs/user-requirements-document.md#1-product-vision';

export const d14: Chapter = {
  id: 'd14',
  slug: 'getting-paid',
  audience: 'driver',
  order: 14,
  title: 'www.guide.d14.title',
  summary: 'www.guide.d14.summary',

  steps: [
    { instruction: 'www.guide.d14.step1' },
    { instruction: 'www.guide.d14.step2', note: 'www.guide.d14.step2.note' },
    { instruction: 'www.guide.d14.step3' },
    { instruction: 'www.guide.d14.step4', note: 'www.guide.d14.step4.note' },
    { instruction: 'www.guide.d14.step5', screenRef: 'SCR-DA-020' },
    { instruction: 'www.guide.d14.step6' },
    { instruction: 'www.guide.d14.step7', note: 'www.guide.d14.step7.note' },
    { instruction: 'www.guide.d14.step8', screenRef: 'SCR-DA-020' },
  ],

  callouts: [
    {
      kind: 'fee',
      body: 'www.guide.d14.callout.cashIsYours',
      source: URD_VISION,
    },
    {
      kind: 'warning',
      body: 'www.guide.d14.callout.qrIsAttested',
      source: URD_QR,
    },
    {
      kind: 'warning',
      body: 'www.guide.d14.callout.payoutsCoverTheWallet',
      source: ADD_PAYOUT,
    },
  ],

  screens: ['SCR-DA-020'],
  relatedChapters: ['d09', 'd13', 'd12'],
  faqRefs: ['driver-keeps', 'how-to-pay', 'why-free'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-27-completing-a-ride-cash-vs-lankaqr-vs-onepay',
    'specs/MageRide_Functional_Walkthrough.md#scenario-48-viewing-the-earnings-summary-and-payment-fee-history',
    'specs/MageRide_Functional_Walkthrough.md#scenario-107-a-passenger-pays-the-drivers-qr-and-both-sides-confirm-it',
    URD_QR,
    URD_VISION,
    ADD_CUSTODY,
    ADD_PAYOUT,
    'specs/architecture-design-document.md#6-service-catalogue-payout-svc',
    'specs/D5_mageride_business_logic.md#8-payment-refund-cod-new-replace-ny-juspay-upi-onepay-lankaqr-cash-d-10',
    'specs/D2_mageride_ui_spec.md#scr-da-022a-scr-di-022a-driver_payout-bank-payout-details-new-al-58-al-59',
    'specs/D2_mageride_ui_spec.md#scr-da-020-scr-di-020-earnings-earnings-dashboard-adapt-ny-driver-earnings-drop-coins',
  ],
};
