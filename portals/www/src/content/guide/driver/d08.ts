/**
 * Driver chapter 8 — the 15-second offer.
 *
 * ## "What a miss costs" — the honest answer is *nothing*, and the specs say so
 *
 * S10's fence: *state it from URD Epic 6A / D1 §B.9 and anchor it. If the specs do
 * not define a penalty for a missed offer, say there is none rather than inventing a
 * consequence — and record the gap.* Read end to end, they do not define one:
 *
 * - **US-6A.3** gives 15 seconds to accept or reject and **US-6A.2** says the request
 *   is *"automatically sent to the next eligible driver, and so on"*. No consequence
 *   is attached to either branch.
 * - **D1 §B.3** is the only line that speaks to it directly, and it says **"no
 *   reputation hit on first timeout"**.
 * - **Walkthrough scenario 28** (declining an offer) is titled *"turn down an offer
 *   without penalty (within reason)"*; its edge cases say repeated declines drop the
 *   **acceptance rate shown on the Level screen** (US-6A.14), and that pattern
 *   rejections of package sizes a driver cannot carry are *"downweighted, not
 *   penalised"*.
 * - Every stated **Level** penalty in Epic 6A is something else: −1 for a **no-show on
 *   an accepted scheduled ride** (US-6A.7), −1 and a temporary delisting for **three
 *   passenger reports** (US-6A.6). Neither is a missed offer.
 *
 * So the chapter says: one miss costs nothing, a habit shows up as a number on your
 * own Level screen. Nothing stronger is available to say.
 *
 * **Gap recorded (the Walkthrough flags it itself, scenario 28):** the specs never
 * state a **threshold** at which a low acceptance rate actively reduces dispatch
 * priority, as opposed to merely being displayed. Until they do, no public page can
 * tell a driver how many declines are too many — so this one does not try.
 *
 * ## The payment row, and the labels this chapter will not print
 *
 * US-6A.2 says the offer card shows *"Payment Method (Cash / LankaQR / Card)"* with
 * *"Card = OnePay"*. **ADD v3.6 §1.18 (AL-57…AL-59, 2026-08-01) retired both card
 * rails as ride methods** — `POST /fare/pay` is `cash | wallet | scan_driver_qr |
 * cod` — so those are two labels for rails a passenger cannot choose. MCS-35 Scope B
 * lists this exact line, and its decision **D3** (*what are C-11's three labels now?*)
 * is still open, which is precisely why this chapter names **no labels at all**: it
 * says the card shows how the passenger chose to pay and leaves the words to the
 * change set. The wireframe frame is safe to publish — it draws `Rs 480 · Cash`.
 *
 * ## The job board belongs here, and its registry entry was corrected
 *
 * A scheduled ride reaches a driver as **this same screen**: intent is posted on the
 * Job Board, and at **T-30 minutes** the system dispatches to the closest
 * intent-poster by Level as a normal offer, *"where the driver accepts or rejects"*
 * (US-6A.5, D2 SCR-DA-017 — *"no accept here"*). S05 had filed SCR-DA-017 under
 * `driver/package-jobs` with the caption *"deliveries you can pick up"*; the board
 * carries **scheduled rides**, not deliveries, in the URD, in D2 and in the drawn
 * frame. Both were corrected in this session.
 */

import type { Chapter } from '@/content/types';

const URD_DISPATCH = 'specs/user-requirements-document.md#epic-6a-driver-dispatch-scheduling-mode-c-standby-on-demand';
const D1_DISPATCH = 'specs/D1_mageride_user_flows.md#b-9-mode-c-ride-dispatch-flow-replace';
const URD_FEES = 'specs/user-requirements-document.md#epic-9-daily-platform-fee-billing';

export const d08: Chapter = {
  id: 'd08',
  slug: 'the-15-second-offer',
  audience: 'driver',
  order: 8,
  title: 'www.guide.d08.title',
  summary: 'www.guide.d08.summary',

  steps: [
    { instruction: 'www.guide.d08.step1', screenRef: 'SCR-DA-014' },
    { instruction: 'www.guide.d08.step2', screenRef: 'SCR-DA-014' },
    {
      instruction: 'www.guide.d08.step3',
      note: 'www.guide.d08.step3.note',
      screenRef: 'SCR-DA-014',
    },
    { instruction: 'www.guide.d08.step4', screenRef: 'SCR-DA-014' },
    { instruction: 'www.guide.d08.step5' },
    { instruction: 'www.guide.d08.step6' },
    { instruction: 'www.guide.d08.step7', screenRef: 'SCR-DA-017' },
    { instruction: 'www.guide.d08.step8', screenRef: 'SCR-DA-017' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.d08.callout.whatAMissCosts',
      source: URD_DISPATCH,
    },
    {
      kind: 'fee',
      body: 'www.guide.d08.callout.secondTripFee',
      source: URD_FEES,
    },
    {
      kind: 'tip',
      body: 'www.guide.d08.callout.offerTaken',
      source: D1_DISPATCH,
    },
  ],

  screens: ['SCR-DA-014', 'SCR-DA-017'],
  relatedChapters: ['d07', 'd09', 'd11', 'd13'],
  faqRefs: ['driver-keeps', 'daily-fee'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-25-receiving-a-ride-offer-the-15-second-accept-window-mode-c',
    'specs/MageRide_Functional_Walkthrough.md#scenario-28-receiving-and-declining-a-ride-offer',
    'specs/MageRide_Functional_Walkthrough.md#scenario-33-viewing-the-job-board-and-posting-intent-for-a-scheduled-ride',
    URD_DISPATCH,
    D1_DISPATCH,
    'specs/D1_mageride_user_flows.md#b-3-per-screen-user-actions-key',
    URD_FEES,
    'specs/D2_mageride_ui_spec.md#scr-da-014-scr-di-014-incoming_request-incoming-dispatch-primary-replace-ny-rideallocationmodal-15s',
    'specs/architecture-design-document.md#1-18-remediation-log-al-57-al-59',
  ],
};
