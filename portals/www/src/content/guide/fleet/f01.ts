/**
 * Fleet chapter 1 — registering your organisation.
 *
 * **The first thing a fleet owner meets is a gate, and this chapter's job is to say
 * so before they spend an afternoon behind it.** US-13.A7: an organisation must be
 * verified and approved *before* it can onboard vehicles or assign drivers, and
 * until then it sits in a **Pending** state with those actions disabled. Walkthrough
 * Scenario 68 puts the same rule in its edge-case table — "Tries to onboard vehicles
 * before approval → Non-read actions blocked". An owner who reads chapter 3 first,
 * uploads a CSV of forty vehicles and watches nothing happen has met a rule the
 * portal enforces correctly and the guide failed to mention.
 *
 * ## Three sign-in methods, and the contrast that has to be drawn
 *
 * US-13.A2: **Email + Password, Google Sign-In, or Apple Sign-In**, all resolving to
 * the same operator account. The URD states this as a *difference* — "This differs
 * from the Driver App, which is Phone OTP only, and the Admin Portal, which is
 * Password or Google Sign-In" — and the difference is worth carrying into the copy,
 * because a fleet owner who also drives has an account on both surfaces reached two
 * different ways, and because `www.faq.signup.a` on this same site answers "a Sri
 * Lankan mobile number", which is the passenger answer and not this one. That FAQ is
 * deliberately **not** in {@link f01.faqRefs} for exactly that reason.
 *
 * ## The sub-roles are named here and explained in chapter 5
 *
 * Team invites are part of organisation setup (US-13.A5, SCR-FP-002's own states
 * line), so Owner / Manager / Viewer has to be named in chapter 1. What each of them
 * can *do* is chapter 5's callout, in one place, because scattering it across six
 * chapters is how a Viewer ends up reading five pages of buttons they do not have.
 */

import type { Chapter } from '@/content/types';

const URD_ACCESS = 'specs/user-requirements-document.md#13a-fleet-portal-access-authentication';
const URD_FLEET =
  'specs/user-requirements-document.md#epic-13-fleet-operator-features-fleet-portal-phase-1';
const WALKTHROUGH_SIGNUP =
  'specs/MageRide_Functional_Walkthrough.md#scenario-67-fleet-owner-sign-up-emailpassword-google-or-apple-sign-in';
const WALKTHROUGH_ORG =
  'specs/MageRide_Functional_Walkthrough.md#scenario-68-setting-up-the-fleet-organisation-kyc-documents-waiting-for-approval';

export const f01: Chapter = {
  id: 'f01',
  slug: 'registering-your-organisation',
  audience: 'fleet',
  order: 1,
  title: 'www.guide.f01.title',
  summary: 'www.guide.f01.summary',

  steps: [
    { instruction: 'www.guide.f01.step1', screenRef: 'SCR-FP-001' },
    { instruction: 'www.guide.f01.step2', note: 'www.guide.f01.step2.note' },
    { instruction: 'www.guide.f01.step3' },
    { instruction: 'www.guide.f01.step4', screenRef: 'SCR-FP-002' },
    { instruction: 'www.guide.f01.step5', note: 'www.guide.f01.step5.note' },
    { instruction: 'www.guide.f01.step6' },
    { instruction: 'www.guide.f01.step7' },
    { instruction: 'www.guide.f01.step8' },
  ],

  callouts: [
    {
      kind: 'warning',
      body: 'www.guide.f01.callout.approvalGate',
      source: URD_ACCESS,
    },
    {
      kind: 'tip',
      body: 'www.guide.f01.callout.threeSubRoles',
      source: URD_ACCESS,
    },
    {
      kind: 'privacy',
      body: 'www.guide.f01.callout.whoSeesYourKyc',
      source: WALKTHROUGH_ORG,
    },
  ],

  screens: ['SCR-FP-001', 'SCR-FP-002'],
  relatedChapters: ['f02', 'f03', 'f05'],
  faqRefs: ['languages', 'modes'],

  sources: [
    URD_FLEET,
    URD_ACCESS,
    WALKTHROUGH_SIGNUP,
    WALKTHROUGH_ORG,
    'specs/D2_mageride_ui_spec.md#scr-fp-001-fleetloginsignup-login-sign-up-new-us-13a2a3',
    'specs/D2_mageride_ui_spec.md#scr-fp-002-fleetorgsetup-organisation-setup-new-us-13a5a7',
  ],
};
