/**
 * Driver chapter 9 — running a trip.
 *
 * Navigate → arrive → **start (OTP)** → end, from Walkthrough scenarios 26, 27 and 35
 * and D1 §B.9's state machine.
 *
 * ## The OTP is named at every step it appears, because it is the first-trip support call
 *
 * S10's fence, and it is right: *a driver who does not know a code is required will
 * call support on their first trip.* So the code is a step of its own, the source of
 * the code is named (**the passenger has it on their screen; ask them for it**), the
 * failure is named (*"Incorrect code"* — re-ask and re-enter), and the consequence is
 * named: **the trip does not start without it.** `DriverArrived → InProgress` is
 * gated on that entry (P-07, D1 §B.9 step 4).
 *
 * ## Calls, after AL-48
 *
 * Walkthrough scenario 26 still says the Call button *"connects without revealing
 * either number"*. **US-26.2 withdrew the masking requirement** (2026-07-05, ADD
 * §1.13 AL-48): a **Free call** is in-app VoIP and a **Normal call** is a direct
 * cellular call to the counterparty's **real number**, revealed after acceptance.
 * Passenger chapter 10 was written against the same correction. Publishing the older
 * sentence would be telling a driver their number is hidden when it is not — a
 * privacy claim, and the one class of claim a public page must not get wrong.
 *
 * **On a third-party booking the driver sees the rider's number, never the booker's**
 * (P-05, retained by US-26.2), and that survives as the useful half.
 *
 * ## Ending it, and the deliberate boundary with the money chapters
 *
 * The fare finalises on **End** and settles by whatever the passenger chose; on a
 * driver-QR ride SCR-DA-015 raises a **"QR payment received?"** confirm and the
 * earning posts on Confirm (US-26.1, AL-47). Two things kept out on purpose: the
 * **rails are not enumerated** — MCS-35 decision D3 (the C-11 label standard, which
 * binds this very card) is open, and the retired `LankaQR / Card` labels must not be
 * printed — and **earnings, the wallet and the payout run are later chapters**. What
 * belongs here is the one rule that changes what a driver does at the drop-off:
 * **the earning posts only once payment is terminal** (R-05).
 *
 * ## Scheduled rides run through this same screen
 *
 * Scenario 35 in one line: an accepted scheduled ride *"proceeds like any active
 * ride"* — Start (code), drive, End. What is different is only the penalty:
 * **failing to appear for an accepted scheduled ride drops the driver's level by
 * one** (US-6A.7), which is one of the two stated Level penalties in the whole of
 * Epic 6A and is worth the reader's attention precisely because a missed *offer*
 * costs nothing (chapter 8).
 *
 * **The five-minute no-show rule is the passenger's, and the driver's half is stated
 * as the specs state it**: after 5 minutes and two reminder texts the passenger is a
 * no-show, they are charged Rs 100 and the driver is compensated (D1 §B.9 step 6,
 * Walkthrough scenario 26). The compensation **amount** is nowhere stated, so the
 * chapter says the driver is compensated and quotes no figure.
 */

import type { Chapter } from '@/content/types';

const D1_DISPATCH = 'specs/D1_mageride_user_flows.md#b-9-mode-c-ride-dispatch-flow-replace';
const URD_MASKING = 'specs/user-requirements-document.md#epic-26-new-2026-07-05-change-set-2-driver-qr-settlement-number-masking-removal';
const URD_DISPATCH = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';

export const d09: Chapter = {
  id: 'd09',
  slug: 'running-a-trip',
  audience: 'driver',
  order: 9,
  title: 'www.guide.d09.title',
  summary: 'www.guide.d09.summary',

  steps: [
    { instruction: 'www.guide.d09.step1', screenRef: 'SCR-DA-015' },
    { instruction: 'www.guide.d09.step2', screenRef: 'SCR-DA-015' },
    {
      instruction: 'www.guide.d09.step3',
      note: 'www.guide.d09.step3.note',
      screenRef: 'SCR-DA-015',
    },
    { instruction: 'www.guide.d09.step4' },
    { instruction: 'www.guide.d09.step5', note: 'www.guide.d09.step5.note' },
    { instruction: 'www.guide.d09.step6', screenRef: 'SCR-DA-015' },
    { instruction: 'www.guide.d09.step7' },
    { instruction: 'www.guide.d09.step8', screenRef: 'SCR-DA-018' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d09.callout.noCodeNoTrip',
      source: D1_DISPATCH,
    },
    {
      kind: 'privacy',
      body: 'www.guide.d09.callout.realNumbers',
      source: URD_MASKING,
    },
    {
      kind: 'warning',
      body: 'www.guide.d09.callout.scheduledNoShow',
      source: URD_DISPATCH,
    },
  ],

  screens: ['SCR-DA-015', 'SCR-DA-018'],
  relatedChapters: ['d08', 'd07', 'd14'],
  faqRefs: ['safety', 'phone-number', 'driver-keeps'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-26-navigating-to-pickup-arriving-and-starting-the-ride-with-the-passenger-code',
    'specs/MageRide_Functional_Walkthrough.md#scenario-35-accepting-and-completing-a-pre-dispatched-scheduled-ride',
    'specs/MageRide_Functional_Walkthrough.md#scenario-108-calls-now-use-real-numbers',
    D1_DISPATCH,
    URD_MASKING,
    URD_DISPATCH,
    'specs/D2_mageride_ui_spec.md#scr-da-015-scr-di-015-ride_active-active-ride-trip-replace-ny-rideactionmodal',
  ],
};
