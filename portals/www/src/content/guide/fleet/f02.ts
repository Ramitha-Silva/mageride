/**
 * Fleet chapter 2 — KYC, and the bank & payout profile.
 *
 * **This chapter exists because of one sentence in US-27.2, and the sentence is a
 * hard gate:** a vehicle *cannot* be set Service payment = Paid, and Paid
 * subscriptions cannot start billing, until the payout profile is **Verified**. An
 * owner who prices a school run, invites forty parents and then discovers that the
 * portal will not let them charge has hit a rule that is correct, documented, and
 * three screens away from where they were working. It is stated here, out loud, and
 * again in chapter 3 where the setting actually lives — not implied by mentioning
 * that a profile exists.
 *
 * ## The screen is Owner-only, and that is a spec fact rather than a guess
 *
 * US-27.1 says "fleet **Owner**"; `specs/D2_mageride_ui_spec.md`'s SCR-FP-002a entry
 * says "**Owner only**"; `specs/wireframes/web_fleet.html` tags the frame's caption
 * `Owner`. Three sources, one answer — which is why the guide can say a Manager
 * cannot open this screen instead of saying nothing and letting one try.
 *
 * ## What the profile is *for*, said in the reader's terms
 *
 * Mode B subscription payments are **pass-through**: they route to the fleet owner,
 * and MageRide holds none of that money (US-27.2, and Walkthrough Scenario 85's
 * closing note). The passenger's pay sheet renders **the owner's own** LankaQR image
 * and verified account details. So the two uploads this screen asks for are not
 * paperwork — the statement-or-passbook page is how the account is proved to belong
 * to the organisation, and the QR image is literally what a subscriber will scan.
 * An owner told that plainly uploads the right two files the first time.
 *
 * ## Two behaviours that are surprising and are therefore steps
 *
 * **Any edit re-enters Pending** (US-27.1), and **payers always see the last verified
 * snapshot, never unverified edits** (US-27.2). The second is the reassuring half of
 * the first and they belong together: changing a branch code does not take the
 * vehicle out of service or show a subscriber a half-edited account number.
 */

import type { Chapter } from '@/content/types';

const URD_PAYOUT =
  'specs/user-requirements-document.md#epic-27-new-2026-07-18-change-set-fleet-portal-payout-vehicle-document-detail';
const D1_PAYOUT_FLOW =
  'specs/D1_mageride_user_flows.md#f-311-fleet-owner-sets-up-the-bank-payout-profile-item-1-us-271272';
const D2_PAYOUT =
  'specs/D2_mageride_ui_spec.md#scr-fp-002a-fleetbankpayout-bank-payout-details-new-us-271272-al-49-owner-only';
const WALKTHROUGH_TRANSFER =
  'specs/MageRide_Functional_Walkthrough.md#scenario-85-verifying-a-bank-transfer-screenshot-uploaded-by-a-subscriber';

export const f02: Chapter = {
  id: 'f02',
  slug: 'kyc-and-your-payout-profile',
  audience: 'fleet',
  order: 2,
  title: 'www.guide.f02.title',
  summary: 'www.guide.f02.summary',

  steps: [
    { instruction: 'www.guide.f02.step1', note: 'www.guide.f02.step1.note', screenRef: 'SCR-FP-002a' },
    { instruction: 'www.guide.f02.step2', note: 'www.guide.f02.step2.note' },
    { instruction: 'www.guide.f02.step3' },
    { instruction: 'www.guide.f02.step4' },
    { instruction: 'www.guide.f02.step5' },
    { instruction: 'www.guide.f02.step6' },
    { instruction: 'www.guide.f02.step7', note: 'www.guide.f02.step7.note' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.f02.callout.paidNeedsVerified',
      source: URD_PAYOUT,
    },
    {
      kind: 'fee',
      body: 'www.guide.f02.callout.passThrough',
      source: URD_PAYOUT,
    },
    {
      kind: 'privacy',
      body: 'www.guide.f02.callout.whatSubscribersSee',
      source: D2_PAYOUT,
    },
  ],

  screens: ['SCR-FP-002a'],
  relatedChapters: ['f01', 'f03', 'f06'],
  faqRefs: ['mode-b-price', 'mode-b-access'],

  sources: [
    URD_PAYOUT,
    D1_PAYOUT_FLOW,
    D2_PAYOUT,
    WALKTHROUGH_TRANSFER,
    'specs/user-requirements-document.md#13a-fleet-portal-access-authentication',
  ],
};
