/**
 * Driver chapter 4 — approval.
 *
 * The whole of URD Epic 2's verification model reduces to one sentence a driver can
 * act on: **a machine approves what it could read, and a person checks everything
 * else.** SCR-DA-006 draws the four verdicts, so the chapter is built around them.
 *
 * ## What auto-verifies, exactly (US-2.23, D2 SCR-DA-006)
 *
 * | Step | Verified when |
 * |---|---|
 * | Vehicle details | the type and registration number were entered |
 * | Insurance | the **expiry date** was extracted |
 * | Revenue licence | the **number and expiry** were extracted |
 * | Front & back photos | the **plate matches** the registration number typed at step 1 |
 *
 * All four Verified → **APPROVED automatically, with no Verification Officer step**.
 * That is a real product decision (Change 6/22) and it is the good news the chapter
 * leads on.
 *
 * ## What sends a step to a person (US-2.10a)
 *
 * Three triggers, and they are not interchangeable: a reading the machine is **not
 * confident** about, a field the **driver typed in** because the scan was unclear,
 * and a **plate that does not match** the registration number. Any one of them sets
 * *that step* Pending and flags it for a Verification Officer to **Confirm** or
 * **Edit & confirm**; the vehicle is not Approved until none remain pending, and
 * every one of those actions is audited.
 *
 * The driver-entered case is the one worth spelling out, because it is the one a
 * driver causes and can avoid: **typing a value is never trusted on its own.** It is
 * the direct consequence of chapter 3's photography, and the two chapters are written
 * to be read in that order.
 *
 * ## "Say plainly what a driver can do while waiting"
 *
 * They already have Home — Phase 1 ends there and no vehicle is required to reach it
 * (D1 §B.7). They can drive a **shared or temporarily assigned** Mode A/B vehicle,
 * because that route does not go through this wizard at all (US-2.25). What they
 * cannot do is go online **on this vehicle**: only an Approved Mode C vehicle can be
 * selected to go live (US-2.26).
 *
 * ## Content gap recorded, not filled
 *
 * **Nothing in the specs states a turnaround time for the Verification Officer
 * queue.** There is no SLA in URD Epic 2, none in D5's onboarding rules, and none in
 * the admin screens that drain the queue (SCR-AP-003/003a). So the chapter tells the
 * driver they are notified by push and in-app when the decision lands (US-2.14) and
 * quotes no duration in either direction. A "usually within 24 hours" on a public
 * page would be an invented commitment, and it is the exact kind a driver would plan
 * a working day around.
 */

import type { Chapter } from '@/content/types';

const URD_VEHICLES = 'specs/user-requirements-document.md#epic-2-vehicle-registration';
const D2_STATUS = 'specs/D2_mageride_ui_spec.md#scr-da-006-scr-di-006-vehicle_onboard_status-vehicle-onboarding-status-replace-change-6-22';

export const d04: Chapter = {
  id: 'd04',
  slug: 'approval',
  audience: 'driver',
  order: 4,
  title: 'www.guide.d04.title',
  summary: 'www.guide.d04.summary',

  steps: [
    { instruction: 'www.guide.d04.step1', screenRef: 'SCR-DA-006' },
    { instruction: 'www.guide.d04.step2', screenRef: 'SCR-DA-006' },
    { instruction: 'www.guide.d04.step3' },
    {
      instruction: 'www.guide.d04.step4',
      note: 'www.guide.d04.step4.note',
      screenRef: 'SCR-DA-006',
    },
    { instruction: 'www.guide.d04.step5' },
    { instruction: 'www.guide.d04.step6' },
    { instruction: 'www.guide.d04.step7' },
  ],

  callouts: [
    {
      kind: 'tip',
      body: 'www.guide.d04.callout.whileYouWait',
      source: URD_VEHICLES,
    },
    {
      kind: 'warning',
      body: 'www.guide.d04.callout.typedIsChecked',
      source: URD_VEHICLES,
    },
  ],

  screens: ['SCR-DA-006'],
  relatedChapters: ['d02', 'd03', 'd07'],
  faqRefs: ['become-a-driver'],

  sources: [
    'specs/MageRide_Functional_Walkthrough.md#scenario-23-new-driver-registration-phone-code-profile-setup-reach-home-then-optional-mode-c-vehicle-onboarding-auto-verified',
    URD_VEHICLES,
    'specs/D1_mageride_user_flows.md#f-25-1-two-phase-per-step-onboarding-with-admin-verify-items-2-6',
    D2_STATUS,
  ],
};
